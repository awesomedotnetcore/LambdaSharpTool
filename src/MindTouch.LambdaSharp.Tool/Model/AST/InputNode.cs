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

    public class InputNode {

        //--- Class Fields ---
        public readonly static Dictionary<string, Func<InputNode, bool>> FieldCheckers = new Dictionary<string, Func<InputNode, bool>> {
            ["Parameter"] = input => input.Parameter != null,
            ["Default"] = input => input.Default != null,
            ["ConstraintDescription"] = input => input.ConstraintDescription != null,
            ["AllowedPattern"] = input => input.AllowedPattern != null,
            ["AllowedValues"] = input => input.AllowedValues != null,
            ["MaxLength"] = input => input.MaxLength != null,
            ["MaxValue"] = input => input.MaxValue != null,
            ["MinLength"] = input => input.MinLength != null,
            ["MinValue"] = input => input.MinValue != null,
            ["Resource"] = input => input.Resource != null,
            ["Import"] = input => input.Import != null,

            // composite checkers
            ["Resource.Properties"] = input => input.Resource?.Properties?.Any() == true
        };

        public static readonly Dictionary<string, IEnumerable<string>> FieldCombinations = new Dictionary<string, IEnumerable<string>> {
            ["Parameter"] = new[] {
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
            ["Import"] = new[] { "Resource" }
        };

        //--- Properties ---

        // common
        public string Section { get; set; }
        public string Label { get; set; }
        public string Description { get; set; }
        public string Type { get; set; } = "String";
        public object Scope { get; set; }
        public bool? NoEcho { get; set; }

        // template input
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

        // cross-module reference
        public string Import { get; set; }
        // public ResourceNode Resource { get; set; }
   }
}