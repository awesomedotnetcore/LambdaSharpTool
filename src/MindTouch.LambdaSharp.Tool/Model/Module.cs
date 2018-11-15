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
using MindTouch.LambdaSharp.Tool.Internal;
using Newtonsoft.Json;
using YamlDotNet.Serialization;

namespace MindTouch.LambdaSharp.Tool.Model {

    public interface IResourceCollection {

        //--- Methods ---
        AResource AddResource(AResource resource);
    }

    public class Module : IResourceCollection {

        //--- Properties ---
        public string Name { get; set; }
        public VersionInfo Version { get; set; }
        public string Description { get; set; }
        public IList<object> Pragmas { get; set; }

        // TODO (2018-11-10, bjorg): there is no reason for this to have object type
        public IList<object> Secrets { get; set; }
        public IList<AResource> Resources { get; set; }
        public IList<AOutput> Outputs { get; set; }
        public IDictionary<string, object> Conditions  { get; set; }
        public IList<Humidifier.Statement> ResourceStatements { get; set; } = new List<Humidifier.Statement>();

        [JsonIgnore]
        public bool HasModuleRegistration => !HasPragma("no-module-registration");

        [JsonIgnore]
        public IEnumerable<Function> Functions => GetAllResources().OfType<Function>();

        //--- Methods ---
        public bool HasPragma(string pragma) => Pragmas?.Contains(pragma) == true;

        public AResource GetResource(string name) {

            // drill down into the parameters collection
            var parts = name.Split("::");
            AResource current = null;
            var parameters = Resources;
            foreach(var part in parts) {
                current = parameters?.FirstOrDefault(p => p.Name == part);
                if(current == null) {
                    break;
                }
                parameters = current.Resources;
            }
            return current ?? throw new KeyNotFoundException(name);
        }

        public IEnumerable<AResource> GetAllResources() {
            var stack = new Stack<IEnumerator<AResource>>();
            stack.Push(Resources.GetEnumerator());
            try {
                while(stack.Any()) {
                    var top = stack.Peek();
                    if(top.MoveNext()) {
                        yield return top.Current;
                        if(top.Current.Resources.Any()) {
                            stack.Push(top.Current.Resources.GetEnumerator());
                        }
                    } else {
                        stack.Pop();
                        try {
                            top.Dispose();
                        } catch { }
                    }
                }
            } finally {
                while(stack.Any()) {
                    var top = stack.Pop();
                    try {
                        top.Dispose();
                    } catch { }
                }
            }
        }

        public InputParameter AddImportParameter(
            string import,
            string type = null,
            IList<string> scope = null,
            string description = null,
            string section = null,
            string label = null,
            bool? noEcho = null
        ) {
            var parts = import.Split("::", 2);
            var exportModule = parts[0];
            var exportName = parts[1];

            // find or create parent collection node
            var parentParameter = Resources.FirstOrDefault(p => p.Name == exportModule);
            if(parentParameter == null) {
                parentParameter = new ValueParameter {
                    Name = exportModule,
                    Description = $"{exportModule} cross-module references",
                    Reference = ""
                };
                AddResource(parentParameter);
            }

            // create imported input
            var result = new InputParameter {
                Name = exportName,
                Default = "$" + import,
                ConstraintDescription = "must either be a cross-module import reference or a non-blank value",
                AllowedPattern =  @"^.+$",

                // set AParameter fields
                Scope = scope ?? new List<string>(),
                Description = description,

                // set AInputParamete fields
                Type = type ?? "String",
                Section = section ?? "Module Settings",

                // TODO (2018-11-11, bjorg): do we really want to use the cross-module reference when converting to a label?
                Label = label ?? StringEx.PascalCaseToLabel(import),
                NoEcho = noEcho
            };
            parentParameter.AddResource(result);

            // add conditional expression
            var condition = $"{result.ResourceName}IsImport";
            result.Reference = AModelProcessor.FnIf(
                condition,
                AModelProcessor.FnImportValue(AModelProcessor.FnSub("${DeploymentPrefix}${Import}", new Dictionary<string, object> {
                    ["Import"] = AModelProcessor.FnSelect("1", AModelProcessor.FnSplit("$", AModelProcessor.FnRef(result.ResourceName)))
                })),
                AModelProcessor.FnRef(result.ResourceName)
            );
            Conditions.Add(condition, AModelProcessor.FnEquals(AModelProcessor.FnSelect("0", AModelProcessor.FnSplit("$", AModelProcessor.FnRef(result.ResourceName))), ""));
            return result;
        }

        public AResource AddResource(AResource resource) {
            resource.ResourceName = resource.Name;
            if(Resources == null) {
                Resources = new List<AResource>();
            }
            Resources.Add(resource);
            return resource;
        }
    }
}