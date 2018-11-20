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

namespace MindTouch.LambdaSharp.Tool.Model {

    public abstract class AModuleEntry {

        //--- Constructors ---
        public AModuleEntry(
            AModuleEntry parent,
            string name,
            string description,
            object reference,
            IList<string> scope
        ) {
            Name = name ?? throw new ArgumentNullException(nameof(name));;
            if(name.Any(c => !char.IsLetterOrDigit(c))) {
                throw new ArgumentException($"invalid name: {name}");
            }
            FullName = (parent == null)
                ? name
                : parent.FullName + "::" + name;
            LogicalId = (parent == null)
                ? name
                : parent.FullName + name;
            ResourceName = "@" + LogicalId;
            Reference = reference;
            Scope = scope ?? new string[0];
        }

        //--- Properties ---
        public string Name { get; }
        public string FullName { get; }
        public string ResourceName { get; }
        public string LogicalId { get; }
        public string Description { get; }
        public IList<string> Scope { get; set; }
        public object Reference { get; set; }
    }

    public class ValueEntry : AModuleEntry {

        //--- Constructors ---
        public ValueEntry(
            AModuleEntry parent,
            string name,
            string description,
            object reference,
            IList<string> scope,
            bool isSecret
        ) : base(parent, name, description, reference, scope) {
            IsSecret = isSecret;
        }

        //--- Properties ---
        public bool IsSecret { get; }
    }

    public class PackageEntry : AModuleEntry {

        //--- Constructors ---
        public PackageEntry(
            AModuleEntry parent,
            string name,
            string description,
            IList<string> scope,
            string sourceFilepath,
            Humidifier.CustomResource package
        ) : base(parent, name, description, null, scope) {
            SourceFilepath = sourceFilepath ?? throw new ArgumentNullException(nameof(sourceFilepath));
            Package = package ?? throw new ArgumentNullException(nameof(package));
        }

        //--- Properties ---
        public string SourceFilepath { get; set; }
        public string PackagePath { get; set; }
        public Humidifier.CustomResource Package { get; set; }
    }

    public class HumidifierEntry : AModuleEntry {

        //--- Constructors ---
        public HumidifierEntry(
            AModuleEntry parent,
            string name,
            string description,
            object reference,
            IList<string> scope,
            Humidifier.Resource resource,
            IList<string> dependsOn,
            string condition
        ) : base(parent, name, description, reference, scope) {
            Resource = resource ?? throw new ArgumentNullException(nameof(resource));
            DependsOn = dependsOn ?? new string[0];
            Condition = condition;
        }

        //--- Properties ---
        public Humidifier.Resource Resource { get; set; }
        public IList<string> DependsOn { get; set; } = new string[0];
        public string Condition { get; set; }
    }

    public class InputEntry : AModuleEntry {

        //--- Constructors ---
        public InputEntry(
            AModuleEntry parent,
            string name,
            string description,
            object reference,
            IList<string> scope,
            string section,
            string label,
            bool isSecret,
            Humidifier.Parameter parameter
        ) : base(parent, name, description, reference, scope) {
            Section = section ?? "Module Settings";
            Label = label ?? StringEx.PascalCaseToLabel(name);
            IsSecret = isSecret;
            Parameter = parameter;
        }

        //--- Properties ---
        public string Section { get; }
        public string Label { get; }
        public bool IsSecret { get; }
        public Humidifier.Parameter Parameter { get; }
    }

    public class FunctionEntry : AModuleEntry {

        //--- Constructors ---
        public FunctionEntry(
            AModuleEntry parent,
            string name,
            string description,
            object reference,
            IList<string> scope,
            string project,
            string language,
            IDictionary<string, object> environment,
            IList<AFunctionSource> sources,
            IList<object> pragmas,
            Humidifier.Lambda.Function function
        ) : base(parent, name, description, reference, scope) {
            Project = project;
            Language = language;
            Environment = environment;
            Sources = sources ?? new AFunctionSource[0];
            Pragmas = pragmas ?? new object[0];
            Function = function ?? throw new ArgumentNullException(nameof(function));
        }

        //--- Properties ---
        public string Project { get; set; }
        public string Language { get; set; }
        public IDictionary<string, object> Environment { get; set; }
        public IList<AFunctionSource> Sources { get; set; }
        public IList<object> Pragmas { get; set; }
        public Humidifier.Lambda.Function Function { get; set; }
        public bool HasFunctionRegistration => !HasPragma("no-function-registration");

        //--- Methods ---
        public bool HasPragma(string pragma) => Pragmas?.Contains(pragma) == true;
   }
}