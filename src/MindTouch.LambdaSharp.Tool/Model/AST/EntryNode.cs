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
                "Allow",
                "Properties",
                "ArnAttribute",
                "EncryptionContext",
                "Pragmas"
            },
            ["Import"] = new[] {
                "Section",
                "Label",
                "Description",
                "Type",
                "Scope",
                "NoEcho",
                "Allow"
            },
            ["Variable"] = new[] {
                "Variable",
                "Description",
                "Type",
                "Scope",
                "Value",
                "EncryptionContext",
                "Entries"
            },
            ["Resource"] = new[] {
                "Description",
                "Type",
                "Scope",
                "Allow",
                "Value",
                "Properties",
                "DependsOn",
                "ArnAttribute",
                "Entries",
                "Pragmas"
            },
            ["Module"] = new[] {
                "Description",
                "Location",
                "DependsOn",
                "Parameters"
            },
            ["Package"] = new[] {
                "Description",
                "Scope",
                "Files"
            },
            ["Function"] = new[] {
                "Description",
                "Memory",
                "Timeout",
                "Project",
                "Runtime",
                "Language",
                "Handler",
                "ReservedConcurrency",
                "VPC",
                "Environment",
                "Sources",
                "Pragmas"
            },
            ["Export"] = new[] {
                "Description",
                "Value"
            },
            ["CustomResource"] = new[] {
                "Description",
                "Handler"
            },
            ["Macro"] = new[] {
                "Description",
                "Handler"
            }
        };

        //--- Properties ---

        /*
         * Parameter: string
         * Section: string
         * Label: string
         * Description: string
         * Type: string
         * Scope: string -or- list<string>
         * NoEcho: bool
         * Default: string
         * ConstraintDescription: string
         * AllowedPattern: string
         * AllowedValues: list<string>
         * MaxLength: int
         * MaxValue: int
         * MinLength: int
         * MinValue: int
         * Allow: string or list<string>
         * Properties: map
         * EncryptionContext: map
         * Pragmas: list<any>
         */
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
        public object Allow { get; set; }
        public IDictionary<string, object> Properties { get; set; }
        public IDictionary<string, string> EncryptionContext { get; set; }
        public IList<object> Pragmas { get; set; }

        /*
         * Import: string
         * Section: string
         * Label: string
         * Description: string
         * Type: string
         * Scope: string -or- list<string>
         * NoEcho: bool
         * Allow: string or list<string>
         */
        public string Import { get; set; }

        /*
         * Variable: string
         * Description: string
         * Type: string
         * Scope: string -or- list<string>
         * Value: any
         * EncryptionContext: map
         * Entries: list<Entry>
         */
        public string Variable { get; set; }
        public object Value { get; set; }
        public IList<EntryNode> Entries { get; set; }

        /*
         * Resource: string
         * Description: string
         * Type: string
         * Scope: string -or- list<string>
         * Allow: string or list<string>
         * Value: any
         * DependsOn: string -or- list<string>
         * Properties: map
         * ArnAttribute: string
         * Pragmas: list<any>
         */
        public string Resource { get; set; }
        public object DependsOn { get; set; }
        public string ArnAttribute { get; set; }

        /*
         * Module: string
         * Description: string
         * Location:
         *   Name: string
         *   Version: string
         *   S3Bucket: string
         * DependsOn: string -or- list<string>
         * Parameters: map
         */
        public string Module { get; set; }
        public ModuleLocation Location { get; set; }
        public IDictionary<string, object> Parameters { get; set; }

        /*
         * Package: string
         * Description: string
         * Scope: string -or- list<string>
         * Files: string
         */
        public string Package { get; set; }
        public string Files { get; set; }

        /*
         * Function: string
         * Description: string
         * Memory: int
         * Timeout: int
         * Project: string
         * Runtime: string
         * Language: string
         * Handler: string
         * ReservedConcurrency: int
         * VPC:
         *  SubnetIds: string -or- list<string>
         *  SecurityGroupIds: string -or- list<string>
         * Environment: map
         * Sources: list<function-source>
         * Pragmas: list<any>
         */
        public string Function { get; set; }
        public string Memory { get; set; }
        public string Timeout { get; set; }
        public string Project { get; set; }
        // public string Handler { get; set; }
        public string Runtime { get; set; }
        public string Language { get; set; }
        public string ReservedConcurrency { get; set; }
        public FunctionVpc VPC { get; set; }
        public Dictionary<string, object> Environment { get; set; }
        public IList<FunctionSourceNode> Sources { get; set; }

        /*
         * Export: string
         * Description: string
         * Value: object
         */
        public string Export { get; set; }

        /*
         * CustomResource: string
         * Description: string
         * Handler: string
         */
        public string CustomResource { get; set; }
        public string Handler { get; set; }
        // public string Description { get; set; }

        /*
         * Macro: string
         * Description: string
         * Handler: object
         */
        public string Macro { get; set; }
    }

    public class FunctionVpc {

        //--- Properties ---
        public object SubnetIds { get; set; }
        public object SecurityGroupIds { get; set; }
    }

    public class ModuleLocation {

        //--- Properties ---
        public object Name { get; set; }
        public object Version { get; set; }
        public object S3Bucket { get; set; }
    }
}