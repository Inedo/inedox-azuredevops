﻿using System.ComponentModel;
using System.Text;
using Inedo.Diagnostics;
using Inedo.Documentation;
using Inedo.ExecutionEngine;
using Inedo.Extensibility;
using Inedo.Extensibility.Operations;
using Inedo.Extensions.AzureDevOps.Client;
using Inedo.Serialization;
using Inedo.Web;

namespace Inedo.Extensions.AzureDevOps.Operations.Issues
{
    [Description("Finds Work Items in Azure DevOps.")]
    [ScriptAlias("Find-WorkItems")]
    public class FindWorkItemsOperation : AzureDevOpsOperation
    {
        [ScriptAlias("IterationPath")]
        [DisplayName("Iteration path")]
        [PlaceholderText("Unchanged")]
        public string IterationPath { get; set; }

        [ScriptAlias("Filter")]
        [DisplayName("Filter")]
        [Description("Filter WIQL that will be appended to the WIQL where clause."
            + "See the <a href=\"https://docs.microsoft.com/en-us/azure/devops/boards/queries/wiql-syntax?view=azure-devops\">Azure DevOps Query Language documentation</a> "
            + "for more information.")]
        public string Filter { get; set; }

        [ScriptAlias("CustomWiql")]
        [DisplayName("Custom WIQL")]
        [PlaceholderText("Use above fields")]
        [FieldEditMode(FieldEditMode.Multiline)]
        [Description("Custom WIQL will ignore the project name and iteration path if supplied. "
            + "See the <a href=\"https://docs.microsoft.com/en-us/azure/devops/boards/queries/wiql-syntax?view=azure-devops\">Azure DevOps Query Language documentation</a> "
            + "for more information.")]
        public string CustomWiql { get; set; }

        [Persistent]
        [DisplayName("Closed states")]
        [Description("The state name used to determined if an issue is closed; when not specified, this defaults to Resolved,Closed,Done.")]
        [Category("Advanced")]
        public string ClosedStates { get; set; } = "Resolved,Closed,Done";

        [Output]
        [DisplayName("Output variable")]
        [ScriptAlias("Output")]
        [Description("The output variable should be a list variable where each item is a map variable. This will include all columns from your WIQL SELECT and \"Id\", \"URL\", and \"IsClosed\". For example: @AzureDevOpsIssueIDs.")]
        public RuntimeValue Output { get; set; }

        public override async Task ExecuteAsync(IOperationExecutionContext context)
        {
            var (c, r) = this.GetCredentialsAndResource(context);
            var client = new AzureDevOpsClient(AH.CoalesceString(r.LegacyInstanceUrl, c.ServiceUrl), c?.Password);
            string wiql = this.GetWiql(context.Log);
            var closedStates = this.ClosedStates.Split(',');

            var workItemsResponse = await client.GetWorkItemsAsync(wiql, context.CancellationToken).ToListAsync().ConfigureAwait(false);
            var columns = workItemsResponse.SelectMany(wi => wi.Fields.Select(f => f.Key)).Distinct().ToArray();
            this.Output = new RuntimeValue(workItemsResponse.Select(wi => getItem(wi)).ToArray());

            RuntimeValue getItem(AdoWorkItem wi)
            {
                var item = new Dictionary<string, RuntimeValue>
                {
                    { "Id", wi.Id.ToString() },
                    { "URL", wi.Links.Html.Href }
                };

                if (columns.Contains("System.State"))
                {
                    if (wi.Fields.ContainsKey("System.State"))
                        item.Add("IsClosed", closedStates.Contains(wi.Fields.GetValueOrDefault("System.State")?.ToString(), StringComparer.OrdinalIgnoreCase));
                    else
                        item.Add("IsClosed", false);
                }

                foreach (var column in columns)
                {
                    if (!item.ContainsKey(column) && wi.Fields.ContainsKey(column))
                        item.Add(column, wi.Fields.GetValueOrDefault(column)?.ToString());
                    else if (!item.ContainsKey(column))
                        item.Add(column, null);
                }
                return new RuntimeValue(item);
            };
        }

        protected override ExtendedRichDescription GetDescription(IOperationConfiguration config)
        {
            var longDescription = new RichDescription();
            longDescription.AppendContent("Get Work Items from ");
            if (config[nameof(this.CustomWiql)] == null)
                longDescription.AppendContent(new Hilite(AH.CoalesceString(config[nameof(this.ProjectName)], config[nameof(this.ResourceName)])),
                " in Azure DevOps for iteration path ",
                new Hilite(this.IterationPath));
            else
                longDescription.AppendContent("custom WIQL");


            return new ExtendedRichDescription(
                new RichDescription("Finds Azure DevOps Work Items"),
                longDescription
            );
        }

        private string GetWiql(ILogSink log)
        {
            if (!string.IsNullOrEmpty(this.CustomWiql))
            {
                log.LogDebug("Using custom WIQL query to filter issues...");
                return this.CustomWiql;
            }

            log.LogDebug($"Constructing WIQL query for project '{this.ProjectName}' and iteration path '{this.IterationPath}'...");

            var buffer = new StringBuilder();
            buffer.Append("SELECT [System.Id], [System.State], [System.Title], [System.Description] FROM WorkItems ");

            bool projectSpecified = !string.IsNullOrEmpty(this.ProjectName);
            bool iterationPathSpecified = !string.IsNullOrEmpty(this.IterationPath);

            if (!projectSpecified && !iterationPathSpecified)
                return buffer.ToString();

            buffer.Append("WHERE ");

            if (projectSpecified)
                buffer.AppendFormat("[System.TeamProject] = '{0}' ", this.ProjectName.Replace("'", "''"));

            if (projectSpecified && iterationPathSpecified)
                buffer.Append("AND ");

            if (iterationPathSpecified)
                buffer.AppendFormat("[System.IterationPath] UNDER '{0}' ", this.IterationPath.Replace("'", "''"));


            bool filterSpecified = !string.IsNullOrEmpty(this.Filter);

            if (projectSpecified && iterationPathSpecified && filterSpecified)
                buffer.Append("AND ");

            if (filterSpecified)
                buffer.Append(this.Filter);

            return buffer.ToString();
        }
    }
}
