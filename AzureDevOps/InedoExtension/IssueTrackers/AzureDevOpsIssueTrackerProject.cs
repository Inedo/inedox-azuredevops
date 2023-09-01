#nullable enable
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using Inedo.Diagnostics;
using Inedo.Documentation;
using Inedo.ExecutionEngine.Variables;
using Inedo.Extensibility.Credentials;
using Inedo.Extensibility.Git;
using Inedo.Extensibility.IssueTrackers;
using Inedo.Extensions.AzureDevOps.Client;
using Inedo.Extensions.AzureDevOps.IssueSources;
using Inedo.Serialization;
using Inedo.Web;

namespace Inedo.Extensions.AzureDevOps.IssueTrackers;

public sealed class AzureDevOpsIssueTrackerService : IssueTrackerService<AzureDevOpsIssueTrackerProject, AzureDevOpsAccount>
{
    public override string DefaultVersionFieldName => "Iteration";
    public override string ServiceName => "Azure DevOps";
    public override bool HasDefaultApiUrl => false;
    public override string PasswordDisplayName => "Personal access token";
    public override string ApiUrlPlaceholderText => "https://dev.azure.com/<my org>";
    public override string ApiUrlDisplayName => "Instance URL";

    protected override async IAsyncEnumerable<string> GetProjectNamesAsync(AzureDevOpsAccount credentials, string? serviceNamespace = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);

        var client = new AzureDevOpsClient(credentials);
        await foreach (var proj in client.GetProjectsAsync(cancellationToken).ConfigureAwait(false))
            yield return proj.Name!;
    }
}

[DisplayName("AzureDevOps Issue Tracker")]
[Description("Work with issues in a AzureDevOps Project")]
public sealed class AzureDevOpsIssueTrackerProject : IssueTrackerProject<AzureDevOpsAccount>
{
    private const string Default_ClosedStates = "Resolved,Closed,Done";
    [Persistent]
    [DisplayName("Closed states")]
    [Category("Advanced Mapping")]
    [PlaceholderText(Default_ClosedStates)]
    [Description("The state name used to determined if an issue is closed; when not specified, this defaults to " + Default_ClosedStates)]
    public string? ClosedStates { get; set; }
    [Persistent]
    [DisplayName("Custom WIQL")]
    [Category("Advanced Mapping")]
    [PlaceholderText("Use above fields")]
    [FieldEditMode(FieldEditMode.Multiline)]
    [Description("Custom WIQL will ignore the project name and iteration path if supplied. "
        + "See the <a href=\"https://docs.microsoft.com/en-us/azure/devops/boards/queries/wiql-syntax?view=azure-devops\">Azure DevOps Query Language documentation</a> "
        + "for more information.")]
    public string? CustomWiql { get; set; }


    public override async Task<IssuesQueryFilter> CreateQueryFilterAsync(IVariableEvaluationContext context)
    {
        if (!string.IsNullOrEmpty(this.CustomWiql))
        {
            try
            {
                var query = (await ProcessedString.Parse(this.CustomWiql).EvaluateValueAsync(context).ConfigureAwait(false)).AsString();
                if (string.IsNullOrEmpty(query))
                    throw new InvalidOperationException("resulting query is an empty string");
                return new WiqlFilter(query);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Could not parse the Issue mapping query \"{this.CustomWiql}\": {ex.Message}");
            }
        }

        try
        {
            var iterationPath = (await ProcessedString.Parse(AH.CoalesceString(this.SimpleVersionMappingExpression, "$ReleaseNumber")).EvaluateValueAsync(context).ConfigureAwait(false)).AsString();
            if (string.IsNullOrEmpty(iterationPath))
                throw new InvalidOperationException("milestone expression is an empty string");

            return new WiqlFilter(this.ProjectName!, iterationPath);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Could not parse the simple mapping expression \"{this.SimpleVersionMappingExpression}\": {ex.Message}");
        }
    }

    public override Task EnsureVersionAsync(IssueTrackerVersion version, ICredentialResolutionContext context, CancellationToken cancellationToken = default)
    {
        this.LogWarning("Azure DevOps does not support closing iteratiotns. Instead, iterations will automatically close once the end date has passed.");
        return Task.CompletedTask;

    }

    public override async IAsyncEnumerable<IssueTrackerIssue> EnumerateIssuesAsync(IIssuesEnumerationContext context, [EnumeratorCancellation]CancellationToken cancellationToken = default)
    {
        var client = this.CreateClient(context);
        var wiql = (WiqlFilter)context.Filter;

        var closedStates = (this.ClosedStates ?? Default_ClosedStates).Split(',').ToHashSet(StringComparer.OrdinalIgnoreCase);
        await foreach (var i in client.GetWorkItemsAsync(wiql.ToWiql(this), cancellationToken).ConfigureAwait(false))
            yield return new AzureDevOpsWorkItem(i, closedStates).ToIssueTrackerIssue();
    }

    public override async IAsyncEnumerable<IssueTrackerVersion> EnumerateVersionsAsync(ICredentialResolutionContext context, [EnumeratorCancellation]CancellationToken cancellationToken = default)
    {
        var client = this.CreateClient(context);
        await foreach (var iteration in client.GetIterationsAsync(this.ProjectName!, cancellationToken).ConfigureAwait(false))
            yield return new(iteration.Name!, iteration.IsClosed);
    }

    public override RichDescription GetDescription() => new(this.ProjectName);

    public override async Task TransitionIssuesAsync(string? fromStatus, string toStatus, string? comment, IIssuesEnumerationContext context, CancellationToken cancellationToken = default)
    {
        var client = this.CreateClient(context);
        var wiql = (WiqlFilter)context.Filter;

        var ct = 0;
        var closedStates = (this.ClosedStates ?? Default_ClosedStates).Split(',').ToHashSet(StringComparer.OrdinalIgnoreCase);
        await foreach (var workItem in client.GetWorkItemsAsync(wiql.ToWiql(this), cancellationToken).ConfigureAwait(false))
        {
            var issue = new AzureDevOpsWorkItem(workItem, closedStates).ToIssueTrackerIssue();
            if (!string.IsNullOrEmpty(fromStatus) && string.Equals(issue.Status, fromStatus, StringComparison.OrdinalIgnoreCase))
            {
                this.LogDebug($"WorkItem {issue.Id} has a status of \"{issue.Status}\"; skipping...");
                continue;
            }
            if (string.Equals(issue.Status, toStatus, StringComparison.OrdinalIgnoreCase))
            {
                this.LogDebug($"WorkItem {issue.Id} is already in status \"{issue.Status}\"; skipping...");
                continue;
            }

            this.LogDebug($"Updating WorkItem {issue.Id} to state \"{toStatus}\"...");
            await client.UpdateWorkItemAsync(issue.Id, null, null, null, toStatus, null, cancellationToken).ConfigureAwait(false);
            ct++;
        }
        this.LogDebug($"{ct} Work Items were updated to to state \"{toStatus}\"...");
    }

    private AzureDevOpsClient CreateClient(ICredentialResolutionContext context)
    {
        var creds = this.GetCredentials(context) as AzureDevOpsAccount
            ?? throw new InvalidOperationException("Credentials are required to query AzureDevOps.");

        return new AzureDevOpsClient(creds, this);

    }
    private sealed class WiqlFilter : IssuesQueryFilter
    {
        public WiqlFilter(string customWiql)
        {
            this.CustomWiql = customWiql;
        }
        public WiqlFilter(string project, string iterationPath)
        {
            this.Project = project;
            this.IterationPath = iterationPath;
        }

        public string? Project { get; }
        public string? IterationPath { get; }
        public string? CustomWiql { get; }

        public string ToWiql(ILogSink? log = null)
        {
            if (!string.IsNullOrEmpty(this.CustomWiql))
            {
                log?.LogDebug($"Ignoring Project/Iteration data and using custom WIQL query to filter issues ({this.CustomWiql})...");
                return this.CustomWiql;
            }

            if (string.IsNullOrEmpty(this.Project))
                throw new InvalidOperationException("Project is required to query issues.");
            if (string.IsNullOrEmpty(this.IterationPath))
                throw new InvalidOperationException("IterationPath is required to query issues.");


            log?.LogDebug($"Constructing WIQL query for project '{this.Project}' and iteration path '{this.IterationPath}'...");
            var buffer = new StringBuilder();
            buffer.Append("SELECT [System.Id], [System.Title], [System.Description], [System.State], [System.CreatedDate], [System.CreatedBy], [System.WorkItemType] FROM WorkItems ");
            buffer.Append("WHERE ");
            buffer.AppendFormat("[System.TeamProject] = '{0}' ", this.Project.Replace("'", "''"));
            buffer.Append("AND ");
            buffer.AppendFormat("[System.IterationPath] UNDER '{0}' ", this.IterationPath.Replace("'", "''"));

            return buffer.ToString();
        }
    }
}
