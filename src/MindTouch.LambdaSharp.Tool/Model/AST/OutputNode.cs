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

namespace MindTouch.LambdaSharp.Tool.Model.AST {

    public class OutputNode {

        //--- Class Fields ---
        public readonly static Dictionary<string, Func<OutputNode, bool>> FieldCheckers = new Dictionary<string, Func<OutputNode, bool>> {
            ["Export"] = output => output.Export != null,
            ["Value"] = output => output.Value != null,
            ["CustomResource"] = output => output.CustomResource != null,
            ["Handler"] = output => output.Handler != null,
            ["Macro"] = output => output.Macro != null,
            ["Handler"] = output => output.Handler != null
        };

        public static readonly Dictionary<string, IEnumerable<string>> FieldCombinations = new Dictionary<string, IEnumerable<string>> {
            ["Export"] = new[] { "Value" },
            ["CustomResource"] = new[] { "Handler" },
            ["Macro"] = new[] { "Handler" }
        };

        //--- Properties ---

        // common
        public string Description { get; set; }

        // stack output
        public string Export { get; set; }
        public object Value { get; set; }

        // custom resource handler
        public string CustomResource { get; set; }
        public string Handler { get; set; }

        // macro
        public string Macro { get; set; }
        // public string Handler { get; set; }
    }
}