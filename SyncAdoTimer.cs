using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Extensions.Timer;   // TimerTrigger + TimerInfo
using Microsoft.Extensions.Logging;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Data;
using System.Data.SqlClient;
using System.Linq;
using System.Globalization;

public class SyncAdoTimer
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<SyncAdoTimer> _log;

    public SyncAdoTimer(IHttpClientFactory httpFactory, ILogger<SyncAdoTimer> log)
    {
        _httpFactory = httpFactory;
        _log = log;
    }

    [Function("SyncAdoTimer")]
    // test : "*/30 * * * * *" 
    public async Task Run([TimerTrigger("0 */1 * * * *")] TimerInfo timerInfo)
    {
        var org        = Get("ADO_ORG");
        var project    = Get("ADO_PROJECT");
        var pat        = Get("ADO_PAT");
        var sqlConnStr = Get("SQL_CONNSTR");

        var http = _httpFactory.CreateClient("ado");
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.ASCII.GetBytes($":{pat}")));
        http.DefaultRequestHeaders.Accept.ParseAdd("application/json;odata.metadata=none");
        var baseUrl = $"https://analytics.dev.azure.com/{org}/{project}/_odata/v3.0-preview";

        _log.LogInformation("Pulling links data");

        // Pull data
        var links = await FetchAll<ODataLink>(http,
            $"{baseUrl}/WorkItemLinks?$filter=LinkTypeReferenceName eq 'System.LinkTypes.Hierarchy-Forward'&$select=SourceWorkItemId,TargetWorkItemId");

        var parentOf = new Dictionary<int,int>();
        foreach (var l in links) parentOf[l.TargetWorkItemId] = l.SourceWorkItemId;

        _log.LogInformation("Pulling programs data");

        var programs = await FetchAll<WiProgram>(http,
            $"{baseUrl}/WorkItems" +
            "?$filter= WorkItemType eq 'SKU' and (State eq 'Signed Off' or State eq 'Validation in-Progress')" +
            "&$top=3000" +
            "&$select=WorkItemId,Title,State,Custom_PlannedSemester,Custom_ProgramStartDate,Custom_ProgramSignOffDate," +
            "Custom_Generation,TagNames,Custom_Program,CreatedDate,Custom_MDMClusterID" +
            "&$expand=AssignedTo($select=UserName,UserEmail)," +
                    "Custom_EngineeringOwner($select=UserName,UserEmail)," +
                    "Custom_PMOwner($select=UserName,UserEmail)" +
            "&$orderby=CreatedDate desc");

        _log.LogInformation("Pulling issues data");

        var issues = await FetchAll<WiIssue>(http,
            $"{baseUrl}/WorkItems" +
            "?$filter=WorkItemType eq 'Issue'" +
            "&$select=" +
                "WorkItemId,Title,ParentWorkItemId,State,AreaSK,ValueArea," +
                "Custom_IssueStartDate,Custom_IssueResolutionDate,Custom_IssueCreatedDate," +
                "Custom_IssueTheme,Custom_IssueScenario," +
                "Custom_ICMLink,Custom_TenantID,Custom_IssuePriority," +
                "Custom_RCAStartDate,Custom_PartnerTeamRCAStartDate," +
                "Custom_RCAOwner" + // string field, not a nav
            "&$expand=" +
                "AssignedTo($select=UserName,UserEmail)," +
                "Custom_StorageOwner($select=UserName,UserEmail)," +
                "Custom_ResolutionBy($select=UserName,UserEmail)" +
            "&$orderby=WorkItemId desc");


        _log.LogInformation("Pulling areas data");
        var areas = await FetchAll<AreaDto>(http, $"{baseUrl}/Areas?$select=AreaSK,AreaPath");
        var areaBySk = areas.ToDictionary(a => a.AreaSK, a => a.AreaPath ?? "");

        _log.LogInformation("Pulling stages data");
        var stages = await FetchAll<WiStage>(http,
            $"{baseUrl}/WorkItems?$select=Title,WorkItemId,WorkItemType,Custom_StageStartDate,Custom_StageEndDate&$filter=WorkItemType eq 'Stage' and (State eq 'Active' or State eq 'Done')");

        // Build DataTables
        var dtPrograms    = MakeProgramsTable();
        var dtRelations   = MakeChildParentTable();
        var dtIssues      = MakeIssuesTable();
        var dtSubPrograms = MakeSubProgramsTable();

        // Fill relations
        foreach (var l in links)
        {
            var r = dtRelations.NewRow();
            r["ParentWorkItemId"] = l.SourceWorkItemId;
            r["ChildWorkItemId"]  = l.TargetWorkItemId;
            r["ChildADOLink"]     = $"https://dev.azure.com/{org}/{project}/_workitems/edit/{l.TargetWorkItemId}";
            dtRelations.Rows.Add(r);
        }

        // Fill SubPrograms (derive parent SKU id, stage type, duration, main title)
        var now = DateTime.UtcNow;
        foreach (var s in stages)
        {
            var r = dtSubPrograms.NewRow();
            r["WorkItemId"]            = s.WorkItemId;
            r["Title"]                 = s.Title;
            r["WorkItemType"]          = s.WorkItemType;
            r["Custom_StageStartDate"] = (object?)s.Custom_StageStartDate ?? DBNull.Value;
            r["Custom_StageEndDate"]   = (object?)s.Custom_StageEndDate ?? DBNull.Value;

            r["MainSKUWorkItemId"] = parentOf.TryGetValue(s.WorkItemId, out var sku) ? sku : (object)DBNull.Value;
            r["StageType"]         = ComputeStageType(s.Title ?? "");

            int? dur = null;
            if (s.Custom_StageStartDate.HasValue)
                dur = (int)Math.Max(0, ((s.Custom_StageEndDate ?? now) - s.Custom_StageStartDate.Value).TotalDays);
            r["DurationDays"] = (object?)dur ?? DBNull.Value;

            if (r["MainSKUWorkItemId"] != DBNull.Value)
            {
                var mainId = (int)r["MainSKUWorkItemId"];
                var title = programs.FirstOrDefault(p => p.WorkItemId == mainId)?.Title;
                r["MainSKUTitle"] = (object?)title ?? DBNull.Value;
            }
            else r["MainSKUTitle"] = DBNull.Value;

            dtSubPrograms.Rows.Add(r);
        }

        // Fill Issues (derive main SKU, durations, areapath/partner + tag)
        foreach (var i in issues)
        {
            var r = dtIssues.NewRow();
            r["WorkItemId"]       = i.WorkItemId;
            r["AreaSK"]           = i.AreaSK.HasValue ? i.AreaSK.Value.ToString("D") : (object)DBNull.Value; // string column
            r["Title"]            = i.Title;
            r["IssueDescription"] = DBNull.Value;

            // direct from custom fields
            r["TenantID"]      = (object?)i.Custom_TenantID ?? DBNull.Value;
            r["IssuePriority"] = (object?)i.Custom_IssuePriority ?? DBNull.Value;
            r["ICMLink"]       = (object?)i.Custom_ICMLink ?? DBNull.Value;

            r["AssignedTo"]   = string.IsNullOrWhiteSpace(FormatUser(i.AssignedTo)) ? (object)DBNull.Value : FormatUser(i.AssignedTo);
            r["StorageOwner"] = string.IsNullOrWhiteSpace(FormatUser(i.Custom_StorageOwner)) ? (object)DBNull.Value : FormatUser(i.Custom_StorageOwner);
            r["ResolutionBy"] = string.IsNullOrWhiteSpace(FormatUser(i.Custom_ResolutionBy)) ? (object)DBNull.Value : FormatUser(i.Custom_ResolutionBy);
            r["RCAOwner"]     = (object?)i.Custom_RCAOwner ?? DBNull.Value;


            r["State"]     = i.State;
            r["ValueArea"] = i.ValueArea;

            r["ParentWorkItemId"]           = (object?)i.ParentWorkItemId ?? DBNull.Value;
            r["Custom_IssueStartDate"]      = (object?)i.Custom_IssueStartDate ?? DBNull.Value;
            r["Custom_IssueResolutionDate"] = (object?)i.Custom_IssueResolutionDate ?? DBNull.Value;
            r["IssueCreatedDate"]           = (object?)i.Custom_IssueCreatedDate ?? DBNull.Value;
            r["RCAStartDate"]               = (object?)i.Custom_RCAStartDate ?? DBNull.Value;
            r["PartnerTeamRCAStartDate"]    = (object?)i.Custom_PartnerTeamRCAStartDate ?? DBNull.Value;

            r["IssueTheme"]    = (object?)i.Custom_IssueTheme ?? DBNull.Value;
            r["IssueScenario"] = (object?)i.Custom_IssueScenario ?? DBNull.Value;

            // derive main SKU by climbing one level (Issue -> parent -> maybe SKU)
            int? mainSku = null;
            if (i.ParentWorkItemId.HasValue && parentOf.TryGetValue(i.ParentWorkItemId.Value, out var pSku))
                mainSku = pSku;
            r["MainSKUWorkItemID"] = (object?)mainSku ?? DBNull.Value;  // NOTE: column name uses ID

            // derive durations
            int? totalDur = null;
            if (i.Custom_IssueStartDate.HasValue)
                totalDur = (int)Math.Max(0, ((i.Custom_IssueResolutionDate ?? now) - i.Custom_IssueStartDate.Value).TotalDays);
            r["DurationinDays"] = (object?)totalDur ?? DBNull.Value;     // NOTE: column name 'in' (lowercase i)

            // Area path + partner team
            var areaPath = (i.AreaSK.HasValue && areaBySk.TryGetValue(i.AreaSK.Value, out var ap)) ? ap : "";
            r["AreaPath"] = areaPath;
            var partner = ComputePartnerTeam(areaPath);
            r["PartnerTeam"]  = partner;
            r["IssueTypeTag"] = partner == "EG Load Perf" ? "EGInternal" : "EGExternal";

            // title of main SKU
            var skuTitle = programs.FirstOrDefault(p => p.WorkItemId == mainSku)?.Title;
            r["TitleMainSKU"] = (object?)skuTitle ?? DBNull.Value;

            r["WorkItemLink"] = $"https://dev.azure.com/{org}/{project}/_workitems/edit/{i.WorkItemId}";
            r["ADO"]          = "ADO";

            // Leave RCA and the RCA_* duration breakdowns for later derivations if applicable
            r["RCA"]                   = DBNull.Value;
            r["RCA_EG_Duration"]       = DBNull.Value;
            r["RCA_Partner_Duration"]  = DBNull.Value;
            r["Issue_Resolution_Duration"] = DBNull.Value;

            dtIssues.Rows.Add(r);
        }

        // Programs and aggregates
        var stagesBySku = dtSubPrograms.AsEnumerable()
            .Where(x => x["MainSKUWorkItemId"] != DBNull.Value)
            .GroupBy(x => (int)x["MainSKUWorkItemId"])
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var p in programs)
        {
            var r = dtPrograms.NewRow();
            r["WorkItemId"]                = p.WorkItemId;
            r["Title"]                     = p.Title;
            r["State"]                     = p.State;
            r["TagNames"]                  = p.TagNames;
            r["Custom_PlannedSemester"]    = p.Custom_PlannedSemester;
            r["Custom_ProgramStartDate"]   = (object?)p.Custom_ProgramStartDate ?? DBNull.Value;
            r["Custom_ProgramSignOffDate"] = (object?)p.Custom_ProgramSignOffDate ?? DBNull.Value;

            int? baseDur = null;
            if (p.Custom_ProgramStartDate.HasValue && p.Custom_ProgramSignOffDate.HasValue)
                baseDur = (int)Math.Max(0, (p.Custom_ProgramSignOffDate.Value - p.Custom_ProgramStartDate.Value).TotalDays);
            r["DurationDays"] = (object?)baseDur ?? DBNull.Value;

            r["Custom_Generation"] = p.Custom_Generation;

            // Owners / MDM (expanded)
            r["MDM_ID"]           = p.Custom_MDMClusterID ?? (object)DBNull.Value;
            r["EngineeringOwner"] = FormatUser(p.Custom_EngineeringOwner) ?? (object)DBNull.Value;
            r["PMOwner"]          = FormatUser(p.Custom_PMOwner) ?? (object)DBNull.Value;
            r["AssignedTo"]       = FormatUser(p.AssignedTo) ?? (object)DBNull.Value;

            var issueForSku = dtIssues.AsEnumerable().Where(x =>
                x["MainSKUWorkItemID"] != DBNull.Value &&
                (int)x["MainSKUWorkItemID"] == p.WorkItemId &&
                x["DurationinDays"] != DBNull.Value);
            var internalDays = issueForSku.Where(x => (string?)x["IssueTypeTag"] == "EGInternal").Sum(x => (int)x["DurationinDays"]);
            var externalDays = issueForSku.Where(x => (string?)x["IssueTypeTag"] == "EGExternal").Sum(x => (int)x["DurationinDays"]);

            r["InternalToEGIssuesDays"] = internalDays;
            r["ExternalToEGIssuesDays"] = externalDays;
            r["RemainingLoadPerfDays"]  = baseDur.HasValue ? Math.Max(0, baseDur.Value - (internalDays + externalDays)) : (object)DBNull.Value;

            stagesBySku.TryGetValue(p.WorkItemId, out var skStages);
            int SumStage(string key) => skStages?.Where(x => string.Equals((string?)x["StageType"], key, StringComparison.OrdinalIgnoreCase)
                                                        && x["DurationDays"] != DBNull.Value)
                                                .Sum(x => (int)x["DurationDays"]) ?? 0;
            int CountStage(string key) => skStages?.Count(x => string.Equals((string?)x["StageType"], key, StringComparison.OrdinalIgnoreCase)) ?? 0;

            r["E2E_DurationDays"]             = SumStage("E2E");
            r["E2E_RunsCount"]                = CountStage("E2E");
            r["PreCheck_DurationDays"]        = SumStage("Pre-Check");
            r["Powercycle_DurationDays"]      = SumStage("Power Cycle");
            r["EN_DurationDays"]              = SumStage("EN Validation");
            r["Throughput_DurationDays"]      = SumStage("Throughput test");
            r["StorageBuildout_DurationDays"] = SumStage("Storage Buildout");
            r["ReportAnalysis_DurationDays"]  = SumStage("Report Analysis");
            r["Investigations_DurationDays"]  = SumStage("Investigations");
            r["FirmwareUpgrade_DurationDays"] = SumStage("Firmware Upgrade");

            if (baseDur.HasValue) r["DurationMonths"] = Math.Round(baseDur.Value / 30.44m, 1);
            var rem = r["RemainingLoadPerfDays"] == DBNull.Value ? (int?)null : (int)r["RemainingLoadPerfDays"];
            if (rem.HasValue) r["RemainingMonths"] = Math.Round(rem.Value / 30.44m, 1);

            r["Area"]             = DBNull.Value;
            r["GrowthSignal"]     = DBNull.Value;
            r["Priority"]         = DBNull.Value;
            r["CapacityTB"]       = DBNull.Value;
            r["TotalSKUSavings"]  = DBNull.Value;
            r["MonthlySavings"]   = DBNull.Value;
            r["Custom_Program"]   = p.Custom_Program ?? (object)DBNull.Value;

            dtPrograms.Rows.Add(r);
        }

        // Bulk load
        using var sql = new SqlConnection(sqlConnStr);
        await sql.OpenAsync();
        using var tx = sql.BeginTransaction();

        async Task Bulk(string table, DataTable dt)
        {
            using (var cmd = new SqlCommand($"TRUNCATE TABLE {table};", sql, tx))
                await cmd.ExecuteNonQueryAsync();
            using var bulk = new SqlBulkCopy(sql, SqlBulkCopyOptions.CheckConstraints, tx)
            {
                DestinationTableName = table,
                BatchSize = 1000
            };
            foreach (DataColumn c in dt.Columns) bulk.ColumnMappings.Add(c.ColumnName, c.ColumnName);
            await bulk.WriteToServerAsync(dt);
        }

        await Bulk("dbo.ChildParentRelations", dtRelations);
        await Bulk("dbo.SubPrograms",          dtSubPrograms);
        await Bulk("dbo.Issues",               dtIssues);
        await Bulk("dbo.Programs",             dtPrograms);

        tx.Commit();

        _log.LogInformation("Loaded → Programs={P}, Issues={I}, Links={L}, SubPrograms={S}",
            dtPrograms.Rows.Count, dtIssues.Rows.Count, dtRelations.Rows.Count, dtSubPrograms.Rows.Count);
    }

    private static string Get(string name) =>
        Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"Missing setting: {name}");

    private static string FormatUser(UserRef? u)
    {
        if (u == null) return "";
        var name  = string.IsNullOrWhiteSpace(u.UserName) ? "" : u.UserName.Trim();
        var email = string.IsNullOrWhiteSpace(u.UserEmail) ? "" : u.UserEmail.Trim();
        if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(email)) return $"{name} <{email}>";
        return name != "" ? name : email;
    }

    private static string ComputeStageType(string title)
    {
        var t = (title ?? "").ToLowerInvariant();
        if (t.Contains("e2e")) return "E2E";
        if (t.Contains("pre-check") || t.Contains("precheck")) return "Pre-Check";
        if (t.Contains("power cycle")) return "Power Cycle";
        if (t.Contains("en validation")) return "EN Validation";
        if (t.Contains("throughput")) return "Throughput test";
        if (t.Contains("storage buildout")) return "Storage Buildout";
        if (t.Contains("report analysis")) return "Report Analysis";
        if (t.Contains("investigations")) return "Investigations";
        if (t.Contains("firmware upgrade")) return "Firmware Upgrade";
        if (t.Contains("component qualification report")) return "NA";
        return "Others";
    }

    private static string ComputePartnerTeam(string? areaPath)
    {
        if (string.IsNullOrWhiteSpace(areaPath)) return "";
        var full = areaPath.Trim();
        var onlySkuValidation = !full.Contains("\\") && full.ToLowerInvariant().Contains("skuqualification");
        if (onlySkuValidation) return "EG Load Perf";
        var first = full.IndexOf('\\');
        if (first < 0) return full;
        var second = full.IndexOf('\\', first + 1);
        if (second > first)
            return full.Substring(first + 1, second - first - 1);
        return full[(first + 1)..];
    }

    private static DataTable MakeProgramsTable()
    {
        var t = new DataTable();
        t.Columns.Add("WorkItemId", typeof(int));
        t.Columns.Add("Title", typeof(string));
        t.Columns.Add("State", typeof(string));
        t.Columns.Add("TagNames", typeof(string));
        t.Columns.Add("Custom_PlannedSemester", typeof(string));
        t.Columns.Add("Custom_ProgramStartDate", typeof(DateTime));
        t.Columns.Add("Custom_ProgramSignOffDate", typeof(DateTime));
        t.Columns.Add("DurationDays", typeof(int));
        t.Columns.Add("Custom_Generation", typeof(string));
        t.Columns.Add("InternalToEGIssuesDays", typeof(int));
        t.Columns.Add("ExternalToEGIssuesDays", typeof(int));
        t.Columns.Add("RemainingLoadPerfDays", typeof(int));
        t.Columns.Add("E2E_DurationDays", typeof(int));
        t.Columns.Add("E2E_RunsCount", typeof(int));
        t.Columns.Add("PreCheck_DurationDays", typeof(int));
        t.Columns.Add("Powercycle_DurationDays", typeof(int));
        t.Columns.Add("EN_DurationDays", typeof(int));
        t.Columns.Add("Throughput_DurationDays", typeof(int));
        t.Columns.Add("StorageBuildout_DurationDays", typeof(int));
        t.Columns.Add("ReportAnalysis_DurationDays", typeof(int));
        t.Columns.Add("Investigations_DurationDays", typeof(int));
        t.Columns.Add("FirmwareUpgrade_DurationDays", typeof(int));
        t.Columns.Add("DurationMonths", typeof(decimal));
        t.Columns.Add("RemainingMonths", typeof(decimal));
        t.Columns.Add("Area", typeof(string));
        t.Columns.Add("MDM_ID", typeof(string));
        t.Columns.Add("GrowthSignal", typeof(string));
        t.Columns.Add("Priority", typeof(string));
        t.Columns.Add("CapacityTB", typeof(decimal));
        t.Columns.Add("EngineeringOwner", typeof(string));
        t.Columns.Add("PMOwner", typeof(string));
        t.Columns.Add("AssignedTo", typeof(string));
        t.Columns.Add("TotalSKUSavings", typeof(decimal));
        t.Columns.Add("MonthlySavings", typeof(decimal));
        t.Columns.Add("Custom_Program", typeof(string));
        return t;
    }

    private static DataTable MakeSubProgramsTable()
    {
        var t = new DataTable();
        t.Columns.Add("WorkItemId", typeof(int));
        t.Columns.Add("Title", typeof(string));
        t.Columns.Add("WorkItemType", typeof(string));
        t.Columns.Add("Custom_StageEndDate", typeof(DateTime));
        t.Columns.Add("Custom_StageStartDate", typeof(DateTime));
        t.Columns.Add("MainSKUWorkItemId", typeof(int));   // parent SKU id (derived via links)
        t.Columns.Add("StageType", typeof(string));        // computed from Title (e.g., "E2E", "Pre-Check", etc.)
        t.Columns.Add("DurationDays", typeof(int));        // (EndDate ?? now) - StartDate
        t.Columns.Add("MainSKUTitle", typeof(string));     // lookup from programs
        return t;
    }


    private static DataTable MakeChildParentTable()
    {
        var t = new DataTable();
        t.Columns.Add("ParentWorkItemId", typeof(int));
        t.Columns.Add("ChildWorkItemId", typeof(int));
        t.Columns.Add("ChildADOLink", typeof(string));
        return t;
    }

    private static DataTable MakeIssuesTable()
    {
        var t = new DataTable();
        t.Columns.Add("WorkItemId", typeof(int));
        t.Columns.Add("AreaSK", typeof(string));                        // store GUID as string
        t.Columns.Add("Title", typeof(string));
        t.Columns.Add("IssueDescription", typeof(string));
        t.Columns.Add("TenantID", typeof(string));
        t.Columns.Add("IssuePriority", typeof(string));
        t.Columns.Add("ICMLink", typeof(string));
        t.Columns.Add("IssueTheme", typeof(string));
        t.Columns.Add("IssueScenario", typeof(string));
        t.Columns.Add("RCAOwner", typeof(string));
        t.Columns.Add("StorageOwner", typeof(string));                  // GUID-as-string
        t.Columns.Add("ResolutionBy", typeof(string));                  // GUID-as-string
        t.Columns.Add("AssignedTo", typeof(string));                    // GUID-as-string
        t.Columns.Add("State", typeof(string));
        t.Columns.Add("ValueArea", typeof(string));
        t.Columns.Add("ParentWorkItemId", typeof(int));
        t.Columns.Add("Custom_IssueResolutionDate", typeof(DateTime));
        t.Columns.Add("Custom_IssueStartDate", typeof(DateTime));
        t.Columns.Add("IssueCreatedDate", typeof(DateTime));
        t.Columns.Add("RCAStartDate", typeof(DateTime));
        t.Columns.Add("PartnerTeamRCAStartDate", typeof(DateTime));
        t.Columns.Add("MainSKUWorkItemID", typeof(int));                // NOTE: ID
        t.Columns.Add("DurationinDays", typeof(int));                   // NOTE: 'in'
        t.Columns.Add("IssueTypeTag", typeof(string));
        t.Columns.Add("TitleMainSKU", typeof(string));
        t.Columns.Add("WorkItemLink", typeof(string));
        t.Columns.Add("ADO", typeof(string));
        t.Columns.Add("RCA", typeof(string));
        t.Columns.Add("AreaPath", typeof(string));
        t.Columns.Add("PartnerTeam", typeof(string));
        t.Columns.Add("RCA_EG_Duration", typeof(int));
        t.Columns.Add("RCA_Partner_Duration", typeof(int));
        t.Columns.Add("Issue_Resolution_Duration", typeof(int));
        return t;
    }

    private sealed class ODataPage<T>
    {
        [JsonPropertyName("value")] public List<T>? Value { get; set; }
        [JsonPropertyName("@odata.nextLink")] public string? NextLink { get; set; }
    }

    private static async Task<List<T>> FetchAll<T>(HttpClient http, string firstUrl)
    {
        var all = new List<T>();
        var url = firstUrl;

        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        options.Converters.Add(new FlexibleStringConverter());

        while (!string.IsNullOrEmpty(url))
        {
            using var res = await http.GetAsync(url);
            res.EnsureSuccessStatusCode();
            var json = await res.Content.ReadAsStringAsync();

            var page = JsonSerializer.Deserialize<ODataPage<T>>(json, options)
                    ?? throw new Exception("Failed to parse OData page.");
            if (page.Value != null) all.AddRange(page.Value);
            url = page.NextLink;
        }
        return all;
    }

    // -------------------- DTOs used by the queries --------------------
    private sealed class ODataLink
    {
        public int SourceWorkItemId { get; set; }
        public int TargetWorkItemId { get; set; }
    }

    private sealed class UserRef
    {
        public string? UserName { get; set; }
        public string? UserEmail { get; set; }
    }

    private sealed class WiProgram
    {
        public int WorkItemId { get; set; }
        public string? Title { get; set; }
        public string? State { get; set; }
        public string? Custom_PlannedSemester { get; set; }
        public DateTime? Custom_ProgramStartDate { get; set; }
        public DateTime? Custom_ProgramSignOffDate { get; set; }
        public string? Custom_Generation { get; set; }
        public string? TagNames { get; set; }
        public string? Custom_Program { get; set; }
        public DateTime? CreatedDate { get; set; }
        public string? Custom_MDMClusterID { get; set; }

        // expanded refs
        public UserRef? AssignedTo { get; set; }
        public UserRef? Custom_EngineeringOwner { get; set; }
        public UserRef? Custom_PMOwner { get; set; }
    }

    private sealed class WiIssue
    {
        public int WorkItemId { get; set; }
        public string? Title { get; set; }
        public int? ParentWorkItemId { get; set; }
        public string? State { get; set; }

        public Guid? AreaSK { get; set; }
        public string? ValueArea { get; set; }

        public DateTime? Custom_IssueStartDate { get; set; }
        public DateTime? Custom_IssueResolutionDate { get; set; }
        public DateTime? Custom_IssueCreatedDate { get; set; }

        public string? Custom_IssueTheme { get; set; }
        public string? Custom_IssueScenario { get; set; }

        public string? Custom_ICMLink { get; set; }
        public string? Custom_TenantID { get; set; }
        public string? Custom_IssuePriority { get; set; }

        public DateTime? Custom_RCAStartDate { get; set; }
        public DateTime? Custom_PartnerTeamRCAStartDate { get; set; }

        // Expanded user refs (names/emails)
        public UserRef? AssignedTo { get; set; }
        public UserRef? Custom_StorageOwner { get; set; }
        public UserRef? Custom_ResolutionBy { get; set; }

        public string? Custom_RCAOwner { get; set; } // string in your feed
    }

    private sealed class AreaDto
    {
        public Guid AreaSK { get; set; }
        public string? AreaPath { get; set; }
    }

    private sealed class WiStage
    {
        public int WorkItemId { get; set; }
        public string? Title { get; set; }
        public string? WorkItemType { get; set; }
        public DateTime? Custom_StageStartDate { get; set; }
        public DateTime? Custom_StageEndDate { get; set; }
    }
}

file sealed class FlexibleStringConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => reader.TryGetInt64(out var i)
                                      ? i.ToString(CultureInfo.InvariantCulture)
                                      : reader.GetDouble().ToString(CultureInfo.InvariantCulture),
            JsonTokenType.True   => "true",
            JsonTokenType.False  => "false",
            JsonTokenType.Null   => null,
            // For objects/arrays/others, capture the raw JSON text
            _ => JsonDocument.ParseValue(ref reader).RootElement.GetRawText()
        };

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
        => writer.WriteStringValue(value);
}
