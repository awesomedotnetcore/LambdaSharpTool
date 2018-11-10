/*
 * MindTouch λ#
 * Copyright (C) 2018 MindTouch, Inc.
 * www.mindtouch.com  oss@mindtouch.com
 *
 * For community documentation and downloads visit mindtouch.com;
 * please review the licensing section.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */



using System;
using System.Collections.Generic;
using System.Linq;

namespace MindTouch.LambdaSharp.Tool.Model.AST {

    public class ModuleDeclareNode {

        //--- Class Fields ---
        public readonly static Dictionary<string, Func<ModuleDeclareNode, bool>> FieldCheckers = new Dictionary<string, Func<ModuleDeclareNode, bool>> {
            ["AllowedPattern"] = decl => decl.AllowedPattern != null,
            ["AllowedValues"] = decl => decl.AllowedValues != null,
            ["Bucket"] = decl => decl.Bucket != null,
            ["ConstraintDescription"] = decl => decl.ConstraintDescription != null,
            ["CustomResource"] = decl => decl.CustomResource != null,
            ["Default"] = decl => decl.Default != null,
            ["Description"] = decl => decl.Description != null,
            ["EncryptionContext"] = decl => decl.EncryptionContext != null,
            ["Environment"] = decl => decl.Environment != null,
            ["Export"] = decl => decl.Export != null,
            ["Files"] = decl => decl.Files != null,
            ["Function"] = decl => decl.Function != null,
            ["Handler"] = decl => decl.Handler != null,
            ["Import"] = decl => decl.Import != null,
            ["Label"] = decl => decl.Label != null,
            ["Language"] = decl => decl.Language != null,
            ["Macro"] = decl => decl.Macro != null,
            ["MaxLength"] = decl => decl.MaxLength != null,
            ["MaxValue"] = decl => decl.MaxValue != null,
            ["Memory"] = decl => decl.Memory != null,
            ["MinLength"] = decl => decl.MinLength != null,
            ["MinValue"] = decl => decl.MinValue != null,
            ["NoEcho"] = decl => decl.NoEcho != null,
            ["Package"] = decl => decl.Package != null,
            ["Parameter"] = decl => decl.Parameter != null,
            ["Pragmas"] = decl => decl.Pragmas != null,
            ["Prefix"] = decl => decl.Prefix != null,
            ["Project"] = decl => decl.Project != null,
            ["ReservedConcurrency"] = decl => decl.ReservedConcurrency != null,
            ["Resource"] = decl => decl.Resource != null,
            ["Runtime"] = decl => decl.Runtime != null,
            ["Scope"] = decl => decl.Scope != null,
            ["Secret"] = decl => decl.Secret != null,
            ["Section"] = decl => decl.Section != null,
            ["Sources"] = decl => decl.Sources != null,
            ["Timeout"] = decl => decl.Timeout != null,
            ["Type"] = decl => decl.Type != null,
            ["Value"] = decl => decl.Value != null,
            ["Var"] = decl => decl.Var != null,
            ["VPC"] = decl => decl.VPC != null,

            // composite checkers
            ["Resource.Properties"] = decl => decl.Resource?.Properties?.Any() == true,
            ["Var.Value"] = decl => decl.Value != null,
            ["Var.Secret"] = decl => decl.Secret != null,
            ["Var.Resource"] = decl => (decl.Value == null) && (decl.Secret == null) && (decl.Resource != null),
            ["Var.Variables"] = decl =>
                (decl.Value == null)
                && (decl.Secret == null)
                && (decl.Resource == null)
                && (decl.Variables?.Any() == true),
        };

        public static readonly Dictionary<string, IEnumerable<string>> FieldCombinations = new Dictionary<string, IEnumerable<string>> {

            // input parameters
            ["Parameter"] = new[] {
                "Default", "ConstraintDescription", "AllowedPattern", "AllowedValues", "MaxLength", "MaxValue", "MinLength", "MinValue",
                "Resource", "Resource.Properties", "Section", "Label", "Description", "Type", "Scope", "NoEcho"
            },
            ["Import"] = new[] { "Resource", "Section", "Label", "Description", "Type", "Scope", "NoEcho" },

            // output values
            ["Export"] = new[] { "Value", "Description" },
            ["CustomResource"] = new[] { "Handler", "Description" },
            ["Macro"] = new[] { "Handler", "Description" },

            // variables
            ["Var.Value"] = new[] { "Var", "Value", "Resource", "Description", "Scope", "Variables" },
            ["Var.Secret"] = new[] { "Var", "Secret", "EncryptionContext", "Description", "Scope", "Variables" },
            ["Var.Resource"] = new[] { "Var", "Resource", "Resource.Properties", "Description", "Scope", "Variables" },
            ["Var.Variables"] = new[] { "Var", "Description", "Scope", "Variables" },
            ["Package"] = new[] { "Files", "Bucket", "Prefix", "Description", "Scope", "Variables" },

            // functions
            ["Function"] = new[] {
                "Description", "Memory", "Timeout", "Project", "Handler", "Runtime", "Language", "ReservedConcurrency",
                "VPC", "Environment", "Sources", "Pragmas"
            }
        };

        //--- Properties ---

        // module parameter
        public string Parameter { get; set; }
        public string Default { get; set; }
        public string ConstraintDescription { get; set; }
        public string AllowedPattern { get; set; }
        public IList<string> AllowedValues { get; set; }
        public int? MaxLength { get; set; }
        public int? MaxValue { get; set; }
        public int? MinLength { get; set; }
        public int? MinValue { get; set; }
        public ResourceNode Resource { get; set; }
        public string Section { get; set; }
        public string Label { get; set; }
        public string Description { get; set; }
        public string Type { get; set; }
        public object Scope { get; set; }
        public bool? NoEcho { get; set; }

        // module import (cross-module reference)
        public string Import { get; set; }
        // public ResourceNode Resource { get; set; }
        // public string Section { get; set; }
        // public string Label { get; set; }
        // public string Description { get; set; }
        // public string Type { get; set; }
        // public object Scope { get; set; }
        // public bool? NoEcho { get; set; }

        // module export value
        public string Export { get; set; }
        public object Value { get; set; }
        // public string Description { get; set; }

        // module export custom resource
        public string CustomResource { get; set; }
        public string Handler { get; set; }
        // public string Description { get; set; }

        // module export macro
        public string Macro { get; set; }
        // public string Description { get; set; }
        // public string Handler { get; set; }

        // value variable
        public string Var { get; set; }
        // public object Value { get; set; }
        // public ResourceNode Resource { get; set; }
        // public string Description { get; set; }
        // public object Scope { get; set; }
        public IList<ParameterNode> Variables { get; set; }

        // secret variable
        // public string Var { get; set; }
        public string Secret { get; set; }
        public IDictionary<string, string> EncryptionContext { get; set; }
        // public string Description { get; set; }
        // public object Scope { get; set; }
        // public IList<ParameterNode> Variables { get; set; }

        // package variable
        public string Package { get; set; }
        public string Files { get; set; }
        public string Bucket { get; set; }
        public string Prefix { get; set; }
        // public string Description { get; set; }
        // public object Scope { get; set; }
        // public IList<ParameterNode> Variables { get; set; }

        // function
        public string Function { get; set; }
        // public string Description { get; set; }
        public string Memory { get; set; }
        public string Timeout { get; set; }
        public string Project { get; set; }
        // public string Handler { get; set; }
        public string Runtime { get; set; }
        public string Language { get; set; }
        public string ReservedConcurrency { get; set; }
        public Dictionary<string, object> VPC { get; set; }
        public Dictionary<string, object> Environment { get; set; }
        public IList<FunctionSourceNode> Sources { get; set; }
        public IList<object> Pragmas { get; set; }
    }
}