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


using System.Collections.Generic;

namespace MindTouch.LambdaSharp.Tool.Model {

    public abstract class AResource : IResourceCollection {

        //--- Properties ---
        public string Name { get; set; }
        public string Description { get; set; }
        public string ResourceName { get; set; }
        public object Reference { get; set; }
        public IList<AResource> Resources { get; set; } = new List<AResource>();

        //--- Methods ---
        public AResource AddResource(AResource resource) {
            resource.ResourceName = ResourceName + resource.Name;
            Resources.Add(resource);
            return resource;
        }
    }

    public abstract class AParameter : AResource {

        //--- Properties ---
        public IList<string> Scope { get; set; } = new string[0];
    }

    public class SecretParameter : AParameter {

        //--- Properties ---
        public object Secret { get; set; }
        public IDictionary<string, string> EncryptionContext { get; set; }
    }

    public class ValueParameter : AParameter { }

    public class PackageParameter : AParameter {

        //--- Properties ---
        public string DestinationBucketParameterName { get; set; }
        public string DestinationKeyPrefix { get; set; }
        public string SourceFilepath { get; set; }
        public string PackagePath { get; set; }
    }

    public abstract class AResourceParameter : AParameter {

        //--- Properties ---
        public Resource Resource { get; set; }
    }

    public class ReferencedResourceParameter : AResourceParameter { }

    public class CloudFormationResourceParameter : AResourceParameter { }

    public class InputParameter : AResourceParameter {

        //--- Properties ---
        public string Section { get; set; }
        public string Label { get; set; }
        public bool? NoEcho { get; set; }
        public string Type { get; set; } = "String";
        public string Default { get; set; }
        public string ConstraintDescription { get; set; }
        public string AllowedPattern { get; set; }
        public IList<string> AllowedValues { get; set; }
        public int? MaxLength { get; set; }
        public int? MaxValue { get; set; }
        public int? MinLength { get; set; }
        public int? MinValue { get; set; }
    }

    public class Function : AResource {

        //--- Properties ---
        public string Memory { get; set; }
        public string Timeout { get; set; }
        public string Project { get; set; }
        public string Handler { get; set; }
        public string Runtime { get; set; }
        public string Language { get; set; }
        public string ReservedConcurrency { get; set; }
        public FunctionVpc VPC;
        public IDictionary<string, object> Environment { get; set; }
        public IList<AFunctionSource> Sources { get; set; }
        public IList<object> Pragmas { get; set; }
        public string PackagePath { get; set; }
        public bool HasFunctionRegistration => !HasPragma("no-function-registration");

        //--- Methods ---
        public bool HasPragma(string pragma) => Pragmas?.Contains(pragma) == true;
   }

   public class FunctionVpc {

       //--- Properties ---
       public object SubnetIds { get; set; }
       public object SecurityGroupIds { get; set; }
   }
}