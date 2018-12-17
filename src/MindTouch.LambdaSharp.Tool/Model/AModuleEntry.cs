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
using System.IO;
using System.Linq;
using MindTouch.LambdaSharp.Tool.Internal;
using Newtonsoft.Json;

namespace MindTouch.LambdaSharp.Tool.Model {
    using static ModelFunctions;

    public abstract class AModuleEntry {

        //--- Constructors ---
        public AModuleEntry(
            AModuleEntry parent,
            string name,
            string description,
            string type,
            IList<string> scope,
            object reference
        ) {
            Name = name ?? throw new ArgumentNullException(nameof(name));;
            FullName = (parent == null)
                ? name
                : parent.FullName + "::" + name;
            Description = description;

            // TODO (2018-11-29, bjorg): logical ID should be computed by module builder to disambiguate hierarchical names when name collisions occur
            LogicalId = (parent == null)
                ? name
                : parent.LogicalId + name;
            ResourceName = "@" + LogicalId;
            Reference = reference;
            Type = type ?? throw new ArgumentNullException(nameof(type));
            Scope = scope ?? new string[0];
        }

        //--- Properties ---
        public string Name { get; }
        public string FullName { get; }
        public string ResourceName { get; }
        public string LogicalId { get; }
        public string Description { get; }
        public string Type { get; }
        public IList<string> Scope { get; set; }
        public object Reference { get; set; }
        public bool DiscardIfNotReachable { get; set; }
        public bool HasSecretType => Type == "Secret";
        public bool HasAwsType => Type.StartsWith("AWS::", StringComparison.Ordinal);

        //--- Abstract Methods ---
        public virtual object GetExportReference() => Reference;
        public virtual bool HasAttribute(string attribute) => false;

        //--- Methods ---
        public virtual bool HasPragma(string pragma) => false;
    }

    public class VariableEntry : AModuleEntry {

        //--- Constructors ---
        public VariableEntry(
            AModuleEntry parent,
            string name,
            string description,
            string type,
            IList<string> scope,
            object reference
        ) : base(parent, name, description, type, scope, reference) { }
    }

    public class PackageEntry : AModuleEntry {

        //--- Constructors ---
        public PackageEntry(
            AModuleEntry parent,
            string name,
            string description,
            IList<string> scope,
            string sourceFilepath
        ) : base(parent, name, description, "String", scope, false) {
            SourceFilepath = sourceFilepath ?? throw new ArgumentNullException(nameof(sourceFilepath));
        }

        //--- Properties ---
        public string SourceFilepath { get; set; }
        public string PackagePath { get; private set; }
    }

    public class InputEntry : AModuleEntry {

        //--- Constructors ---
        public InputEntry(
            AModuleEntry parent,
            string name,
            string section,
            string label,
            string description,
            string type,
            IList<string> scope,
            object reference,
            Humidifier.Parameter parameter
        ) : base(parent, name, description, type, scope, reference) {
            Section = section ?? "Module Settings";
            Label = label ?? StringEx.PascalCaseToLabel(name);
            Parameter = parameter;
        }

        //--- Properties ---
        public string Section { get; }
        public string Label { get; }
        public Humidifier.Parameter Parameter { get; }
    }

    public abstract class AResourceEntry : AModuleEntry {

        //--- Constructors ---
        public AResourceEntry(
            AModuleEntry parent,
            string name,
            string description,
            string type,
            IList<string> scope,
            object reference
        ) : base(parent, name, description, type, scope, reference) { }

        //--- Methods ---
        public override bool HasAttribute(string attribute) => ResourceMapping.HasAttribute(Type, attribute);
    }

    public class ResourceEntry : AResourceEntry {

        //--- Constructors ---
        public ResourceEntry(
            AModuleEntry parent,
            string name,
            string description,
            IList<string> scope,
            Humidifier.Resource resource,
            string resourceArnAttribute,
            IList<string> dependsOn,
            string condition,
            IList<object> pragmas
        ) : base(parent, name, description, resource.AWSTypeName, scope, reference: null) {
            Resource = resource ?? throw new ArgumentNullException(nameof(resource));
            ResourceArnAttribute = resourceArnAttribute;
            DependsOn = dependsOn ?? new string[0];
            Condition = condition;
            Pragmas = pragmas ?? new object[0];
        }

        //--- Properties ---
        public Humidifier.Resource Resource { get; set; }
        public string ResourceArnAttribute { get; set; }
        public IList<string> DependsOn { get; set; }
        public string Condition { get; set; }
        public IList<object> Pragmas { get; }

        //--- Methods ---
        public override object GetExportReference()
            => (ResourceArnAttribute != null)
                ? FnGetAtt(ResourceName, ResourceArnAttribute)
                : HasAttribute("Arn")
                ? FnGetAtt(ResourceName, "Arn")
                : FnRef(ResourceName);

        public override bool HasPragma(string pragma) => Pragmas.Contains(pragma);
    }

    public class FunctionEntry : AResourceEntry {

        //--- Constructors ---
        public FunctionEntry(
            AModuleEntry parent,
            string name,
            string description,
            IList<string> scope,
            string project,
            string language,
            IDictionary<string, object> environment,
            IList<AFunctionSource> sources,
            IList<object> pragmas,
            Humidifier.Lambda.Function function
        ) : base(parent, name, description, function.AWSTypeName, scope, reference: null) {
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
        public override object GetExportReference() => FnGetAtt(ResourceName, "Arn");

        public override bool HasPragma(string pragma) => Pragmas.Contains(pragma);
    }
}