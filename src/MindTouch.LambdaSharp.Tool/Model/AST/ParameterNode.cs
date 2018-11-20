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
using YamlDotNet.Serialization;

namespace MindTouch.LambdaSharp.Tool.Model.AST {

    public class ParameterNode {

        //--- Class Fields ---
        public readonly static Dictionary<string, Func<ParameterNode, bool>> FieldCheckers = new Dictionary<string, Func<ParameterNode, bool>> {
            ["Var"] = parameter => parameter.Var != null,
            ["Value"] = parameter => parameter.Value != null,
            ["Resource"] = parameter => parameter.Resource != null,
            ["Secret"] = parameter => parameter.Secret != null,
            ["EncryptionContext"] = parameter => parameter.EncryptionContext != null,
            ["Package"] = parameter => parameter.Package != null,
            ["Files"] = parameter => parameter.Files != null,
            ["Bucket"] = parameter => parameter.Bucket != null,
            ["Prefix"] = parameter => parameter.Prefix != null,

            // composite checkers
            ["Var.Value"] = parameter => (parameter.Value != null) && (parameter.Resource == null),
            ["Var.Secret"] = parameter => parameter.Secret != null,
            ["Var.Reference"] = parameter => (parameter.Value != null) && (parameter.Resource != null),
            ["Var.Resource"] = parameter => (parameter.Value == null) && (parameter.Resource != null),
            ["Var.Empty"] = parameter =>
                (parameter.Value == null)
                && (parameter.Secret == null)
                && (parameter.Resource == null)
                && (parameter.Package == null),
            ["Resource.Properties"] = input => input.Resource?.Properties?.Any() == true
        };

        public static readonly Dictionary<string, IEnumerable<string>> FieldCombinations = new Dictionary<string, IEnumerable<string>> {
            ["Var.Value"] = new[] { "Var", "Value" },
            ["Var.Secret"] = new[] { "Var", "Secret", "EncryptionContext" },
            ["Var.Reference"] = new[] { "Var", "Value", "Resource" },
            ["Var.Resource"] = new[] { "Var", "Resource", "Resource.Properties" },
            ["Var.Empty"] = new[] { "Var" },
            ["Package"] = new[] { "Files", "Bucket", "Prefix" }
        };

        //--- Properties ---

        // common
        public string Description { get; set; }
        public object Scope { get; set; }
        public IList<ParameterNode> Variables { get; set; }

        // value
        public string Var { get; set; }
        public object Value { get; set; }
        public ResourceNode Resource { get; set; }

        // secret
        // public string Var { get; set; }
        public string Secret { get; set; }
        public IDictionary<string, string> EncryptionContext { get; set; }

        // package
        public string Package { get; set; }
        public string Files { get; set; }
        public object Bucket { get; set; }
        public object Prefix { get; set; }
    }
}