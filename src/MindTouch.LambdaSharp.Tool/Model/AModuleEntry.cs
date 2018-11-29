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

    public abstract class AModuleEntry {

        //--- Constructors ---
        public AModuleEntry(
            AModuleEntry parent,
            string name,
            string description,
            object reference,
            IList<string> scope,
            bool isSecret
        ) {
            Name = name ?? throw new ArgumentNullException(nameof(name));;
            FullName = (parent == null)
                ? name
                : parent.FullName + "::" + name;

            // TODO (2018-11-29, bjorg): logical ID should be computed by module builder to disambiguate hierarchical names when name collisions occur
            LogicalId = (parent == null)
                ? name
                : parent.LogicalId + name;
            ResourceName = "@" + LogicalId;
            Reference = reference;
            Scope = scope ?? new string[0];
            IsSecret = isSecret;
        }

        //--- Properties ---
        public string Name { get; }
        public string FullName { get; }
        public string ResourceName { get; }
        public string LogicalId { get; }
        public string Description { get; }
        public IList<string> Scope { get; set; }
        public object Reference { get; set; }
        public bool IsSecret { get; }

        //--- Abstract Methods ---
        public abstract object GetExportReference();
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
        ) : base(parent, name, description, reference, scope, isSecret) { }

        //--- Methods ---
        public override object GetExportReference() => Reference;
    }

    public class PackageEntry : AModuleEntry {

        //--- Constructors ---
        public PackageEntry(
            AModuleEntry parent,
            string name,
            string description,
            IList<string> scope,
            object destinationBucket,
            object destinationKeyPrefix,
            string sourceFilepath
        ) : base(parent, name, description, null, scope, false) {
            SourceFilepath = sourceFilepath ?? throw new ArgumentNullException(nameof(sourceFilepath));
            Package = new Humidifier.CustomResource("LambdaSharp::S3::Package") {
                ["DestinationBucketArn"] = destinationBucket,
                ["DestinationKeyPrefix"] = destinationKeyPrefix,
                ["SourceBucketName"] = AModelProcessor.FnRef("DeploymentBucketName"),
                ["SourcePackageKey"] = "<MISSING>"
            };
        }

        //--- Properties ---
        public string SourceFilepath { get; set; }
        public string PackagePath { get; private set; }
        public Humidifier.CustomResource Package { get; set; }

        //--- Methods ---
        public override object GetExportReference() => Reference;

        public void UpdatePackagePath(string package) {
            PackagePath = package ?? throw new ArgumentNullException(nameof(package));
            Package["SourcePackageKey"] = AModelProcessor.FnSub($"Modules/${{Module::Name}}/Assets/{Path.GetFileName(package)}");
        }
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
            string resourceArnAttribute,
            IList<string> dependsOn,
            string condition
        ) : base(parent, name, description, reference, scope, false) {
            Resource = resource ?? throw new ArgumentNullException(nameof(resource));
            ResourceArnAttribute = resourceArnAttribute;
            DependsOn = dependsOn ?? new string[0];
            Condition = condition;
        }

        //--- Properties ---
        public Humidifier.Resource Resource { get; set; }
        public string ResourceArnAttribute { get; set; }
        public IList<string> DependsOn { get; set; } = new string[0];
        public string Condition { get; set; }

        //--- Methods ---
        public override object GetExportReference()
            => (ResourceArnAttribute != null)
                ? AModelProcessor.FnGetAtt(ResourceName, ResourceArnAttribute)
                : ResourceMapping.HasAttribute(Resource.AWSTypeName, "Arn")
                ? AModelProcessor.FnGetAtt(ResourceName, "Arn")
                : AModelProcessor.FnRef(ResourceName);
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
        ) : base(parent, name, description, reference, scope, isSecret) {
            Section = section ?? "Module Settings";
            Label = label ?? StringEx.PascalCaseToLabel(name);
            Parameter = parameter;
        }

        //--- Properties ---
        public string Section { get; }
        public string Label { get; }
        public Humidifier.Parameter Parameter { get; }

        //--- Methods ---
        public override object GetExportReference() => Reference;
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
        ) : base(parent, name, description, reference, scope, false) {
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
        public override object GetExportReference() => AModelProcessor.FnGetAtt(ResourceName, "Arn");

        public bool HasPragma(string pragma) => Pragmas?.Contains(pragma) == true;

        public void UpdatePackagePath(string package) {
            Function.Code = new Humidifier.Lambda.FunctionTypes.Code {
                S3Bucket = AModelProcessor.FnRef("DeploymentBucketName"),
                S3Key = AModelProcessor.FnSub($"Modules/${{Module::Name}}/Assets/{Path.GetFileName(package)}")
            };
        }
   }
}