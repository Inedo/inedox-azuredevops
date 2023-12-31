﻿using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using Inedo.Diagnostics;
using Inedo.Documentation;
using Inedo.Extensibility.Credentials;
using Inedo.Extensibility.IssueSources;
using Inedo.Extensibility.SecureResources;
using Inedo.Extensions.AzureDevOps.Client;
using Inedo.Extensions.AzureDevOps.SuggestionProviders;
using Inedo.Serialization;
using Inedo.Web;

namespace Inedo.Extensions.AzureDevOps.IssueSources
{
    [DisplayName("Azure DevOps Issue Source")]
    [Description("Issue source for Azure DevOps.")]
    public sealed class AzureDevOpsIssueSource : IssueSource<AzureDevOpsRepository>, IMissingPersistentPropertyHandler
    {
        [Persistent]
        [DisplayName("Project")]
        [SuggestableValue(typeof(ProjectNameSuggestionProvider))]
        public string Project { get; set; }
        [Persistent]
        [DisplayName("Iteration path")]
        public string IterationPath { get; set; }
        [Persistent]
        [DisplayName("Closed states")]
        [Description("The state name used to determined if an issue is closed; when not specified, this defaults to Resolved,Closed,Done.")]
        public string ClosedStates { get; set; } = "Resolved,Closed,Done";
        [Persistent]
        [DisplayName("Custom WIQL")]
        [PlaceholderText("Use above fields")]
        [FieldEditMode(FieldEditMode.Multiline)]
        [Description("Custom WIQL will ignore the project name and iteration path if supplied. "
            + "See the <a href=\"https://docs.microsoft.com/en-us/azure/devops/boards/queries/wiql-syntax?view=azure-devops\">Azure DevOps Query Language documentation</a> "
            + "for more information.")]
        public string CustomWiql { get; set; }

        void IMissingPersistentPropertyHandler.OnDeserializedMissingProperties(IReadOnlyDictionary<string, string> missingProperties)
        {
            if (string.IsNullOrEmpty(this.ResourceName) && missingProperties.TryGetValue("CredentialName", out var value))
                this.ResourceName = value;
        }

        public override async IAsyncEnumerable<IIssueTrackerIssue> EnumerateIssuesAsync(IIssueSourceEnumerationContext context, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var resource = SecureResource.TryCreate(SecureResourceType.GitRepository, this.ResourceName, new ResourceResolutionContext(context.ProjectId)) as AzureDevOpsRepository;
            var credentials = resource?.GetCredentials(new CredentialResolutionContext(context.ProjectId, null)) as AzureDevOpsAccount;
            if (resource == null)
                throw new InvalidOperationException("A resource must be supplied to enumerate AzureDevOps issues.");

            var client = new AzureDevOpsClient(AH.CoalesceString(resource.LegacyInstanceUrl, credentials.ServiceUrl), credentials?.Password );
            var wiql = this.GetWiql(context.Log);

            var closedStates = this.ClosedStates.Split(',').ToHashSet(StringComparer.OrdinalIgnoreCase);

            await foreach (var i in client.GetWorkItemsAsync(wiql, cancellationToken).ConfigureAwait(false))
                yield return new AzureDevOpsWorkItem(i, closedStates);
        }

        private string GetWiql(ILogSink log)
        {
            if (!string.IsNullOrEmpty(this.CustomWiql))
            {
                log.LogDebug("Using custom WIQL query to filter issues...");
                return this.CustomWiql;
            }

            log.LogDebug($"Constructing WIQL query for project '{this.Project}' and iteration path '{this.IterationPath}'...");

            var buffer = new StringBuilder();
            buffer.Append("SELECT [System.Id], [System.Title], [System.Description], [System.State], [System.CreatedDate], [System.CreatedBy], [System.WorkItemType] FROM WorkItems ");

            bool projectSpecified = !string.IsNullOrEmpty(this.Project);
            bool iterationPathSpecified = !string.IsNullOrEmpty(this.IterationPath);

            if (!projectSpecified && !iterationPathSpecified)
                return buffer.ToString();

            buffer.Append("WHERE ");

            if (projectSpecified)
                buffer.AppendFormat("[System.TeamProject] = '{0}' ", this.Project.Replace("'", "''"));

            if (projectSpecified && iterationPathSpecified)
                buffer.Append("AND ");

            if (iterationPathSpecified)
                buffer.AppendFormat("[System.IterationPath] UNDER '{0}' ", this.IterationPath.Replace("'", "''"));

            return buffer.ToString();
        }

        public override RichDescription GetDescription()
        {
            if (!string.IsNullOrEmpty(this.CustomWiql))
                return new RichDescription("Get Issues from Azure DevOps Using Custom WIQL");
            else
                return new RichDescription(
                    "Get Issues from ", new Hilite(AH.CoalesceString(this.Project, this.ResourceName)), " in Azure DevOps for iteration path ", new Hilite(this.IterationPath)
                );
        }
    }
}
