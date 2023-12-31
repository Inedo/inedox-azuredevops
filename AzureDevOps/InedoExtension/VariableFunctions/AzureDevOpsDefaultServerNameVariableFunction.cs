﻿using System.ComponentModel;
using Inedo.Documentation;
using Inedo.Extensibility;
using Inedo.Extensibility.VariableFunctions;

namespace Inedo.Extensions.AzureDevOps.VariableFunctions
{
    [Undisclosed]
    [ScriptAlias("AzureDevOpsDefaultServerName")]
    [Description("[obsolete] The name of the server to connect to when browsing from the web UI; otherwise a local agent is used.")]
    [ExtensionConfigurationVariable(Required = false)]
    public sealed class AzureDevOpsDefaultServerNameVariableFunction : ScalarVariableFunction
    {
        protected override object EvaluateScalar(IVariableFunctionContext context) => string.Empty;
    }
}
