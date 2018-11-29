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

    public class EntryNode {

        //--- Class Fields ---
        public readonly static Dictionary<string, Func<EntryNode, bool>> FieldCheckers = new Dictionary<string, Func<EntryNode, bool>> {
            ["AllowedPattern"] = entry => entry.AllowedPattern != null,
            ["AllowedValues"] = entry => entry.AllowedValues != null,
            ["Bucket"] = entry => entry.Bucket != null,
            ["ConstraintDescription"] = entry => entry.ConstraintDescription != null,
            ["CustomResource"] = entry => entry.CustomResource != null,
            ["Default"] = entry => entry.Default != null,
            ["Description"] = entry => entry.Description != null,
            ["EncryptionContext"] = entry => entry.EncryptionContext != null,
            ["Environment"] = entry => entry.Environment != null,
            ["Entries"] = entry => entry.Entries != null,
            ["Export"] = entry => entry.Export != null,
            ["Files"] = entry => entry.Files != null,
            ["Function"] = entry => entry.Function != null,
            ["Handler"] = entry => entry.Handler != null,
            ["Import"] = entry => entry.Import != null,
            ["Label"] = entry => entry.Label != null,
            ["Language"] = entry => entry.Language != null,
            ["Macro"] = entry => entry.Macro != null,
            ["MaxLength"] = entry => entry.MaxLength != null,
            ["MaxValue"] = entry => entry.MaxValue != null,
            ["Memory"] = entry => entry.Memory != null,
            ["MinLength"] = entry => entry.MinLength != null,
            ["MinValue"] = entry => entry.MinValue != null,
            ["NoEcho"] = entry => entry.NoEcho != null,
            ["Package"] = entry => entry.Package != null,
            ["Parameter"] = entry => entry.Parameter != null,
            ["Pragmas"] = entry => entry.Pragmas != null,
            ["Prefix"] = entry => entry.Prefix != null,
            ["Project"] = entry => entry.Project != null,
            ["ReservedConcurrency"] = entry => entry.ReservedConcurrency != null,
            ["Resource"] = entry => entry.Resource != null,
            ["Resource"] = entry => entry.Resource != null,
            ["Runtime"] = entry => entry.Runtime != null,
            ["Scope"] = entry => entry.Scope != null,
            ["Secret"] = entry => entry.Secret != null,
            ["Section"] = entry => entry.Section != null,
            ["Sources"] = entry => entry.Sources != null,
            ["Timeout"] = entry => entry.Timeout != null,
            ["Type"] = entry => entry.Type != null,
            ["Value"] = entry => entry.Value != null,
            ["Var"] = entry => entry.Var != null,
            ["Variables"] = entry => entry.Variables != null,
            ["VPC"] = entry => entry.VPC != null,

            // composite checkers
            ["Var.Value"] = entry => (entry.Var != null) && (entry.Value != null) && (entry.Resource == null),
            ["Var.Secret"] = entry => (entry.Var != null) && (entry.Secret != null),
            ["Var.Reference"] = entry => (entry.Var != null) && (entry.Value != null) && (entry.Resource != null),
            ["Var.Resource"] = entry => (entry.Var != null) && (entry.Value == null) && (entry.Resource != null),
            ["Var.Empty"] = entry =>
                (entry.Var != null)
                && (entry.Value == null)
                && (entry.Secret == null)
                && (entry.Resource == null)
                && (entry.Package == null),
            ["Resource.Properties"] = entry => entry.Resource?.Properties?.Any() == true
        };

        public static readonly Dictionary<string, IEnumerable<string>> FieldCombinations = new Dictionary<string, IEnumerable<string>> {
            ["Parameter"] = new[] {
                "Section",
                "Label",
                "Description",
                "Type",
                "Scope",
                "NoEcho",
                "Default",
                "ConstraintDescription",
                "AllowedPattern",
                "AllowedValues",
                "MaxLength",
                "MaxValue",
                "MinLength",
                "MinValue",
                "Resource",
                "Resource.Properties"
            },
            ["Import"] = new[] {
                "Section",
                "Label",
                "Description",
                "Type",
                "Scope",
                "NoEcho",
                "Resource"
            },
            ["Var.Value"] = new[] {
                "Var",
                "Description",
                "Scope",
                "Value",
                "Variables",
                "Entries"
            },
            ["Var.Secret"] = new[] {
                "Var",
                "Description",
                "Scope",
                "Secret",
                "EncryptionContext",
                "Variables",
                "Entries"
            },
            ["Var.Reference"] = new[] {
                "Var",
                "Description",
                "Scope",
                "Value",
                "Resource",
                "Variables",
                "Entries"
            },
            ["Var.Resource"] = new[] {
                "Var",
                "Description",
                "Scope",
                "Resource",
                "Resource.Properties",
                "Variables",
                "Entries"
            },
            ["Var.Empty"] = new[] {
                "Var",
                "Scope",
                "Description",
                "Variables",
                "Entries"
            },
            ["Package"] = new[] {
                "Description",
                "Scope",
                "Files",
                "Bucket",
                "Prefix"
            },
            ["Function"] = new[] {
                "Description",
                "Memory",
                "Timeout",
                "Project",
                "Handler",
                "Runtime",
                "Language",
                "ReservedConcurrency",
                "VPC",
                "Environment",
                "Sources",
                "Pragmas"
            },
            ["Export"] = new[] {
                "Value",
                "Description"
            },
            ["CustomResource"] = new[] {
                "Handler",
                "Description"
            },
            ["Macro"] = new[] {
                "Handler",
                "Description"
            }
        };

        //--- Properties ---

        // parameter entry
        public string Parameter { get; set; }
        public string Section { get; set; }
        public string Label { get; set; }
        public string Description { get; set; }
        public string Type { get; set; }
        public object Scope { get; set; }
        public bool? NoEcho { get; set; }
        public string Default { get; set; }
        public string ConstraintDescription { get; set; }
        public string AllowedPattern { get; set; }
        public IList<string> AllowedValues { get; set; }
        public int? MaxLength { get; set; }
        public int? MaxValue { get; set; }
        public int? MinLength { get; set; }
        public int? MinValue { get; set; }
        public ResourceNode Resource { get; set; }

        // cross-module import entry
        public string Import { get; set; }
        // public string Section { get; set; }
        // public string Label { get; set; }
        // public string Description { get; set; }
        // public string Type { get; set; } = "String";
        // public object Scope { get; set; }
        // public bool? NoEcho { get; set; }
        // public ResourceNode Resource { get; set; }

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

        // value entry
        public string Var { get; set; }
        // public string Description { get; set; }
        // public object Scope { get; set; }
        public IList<EntryNode> Variables { get; set; }
        public IList<EntryNode> Entries { get; set; }
        // public object Value { get; set; }
        // public ResourceNode Resource { get; set; }

        // secret entry
        // public string Var { get; set; }
        // public string Description { get; set; }
        // public object Scope { get; set; }
        // public IList<ParameterNode> Variables { get; set; }
        public string Secret { get; set; }
        public IDictionary<string, string> EncryptionContext { get; set; }

        // package entry
        public string Package { get; set; }
        // public string Description { get; set; }
        // public object Scope { get; set; }
        // public IList<ParameterNode> Variables { get; set; }
        public string Files { get; set; }
        public object Bucket { get; set; }
        public object Prefix { get; set; }

        // function entry
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