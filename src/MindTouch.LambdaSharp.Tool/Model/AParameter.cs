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
using Newtonsoft.Json;

namespace MindTouch.LambdaSharp.Tool.Model {

    public abstract class AResource {

        //--- Properties ---
        public string Name { get; set; }
        public string Description { get; set; }
        public IList<string> Scope { get; set; } = new string[0];
        public object Reference;
    }

    public class SecretParameter : AResource { }

    public class ValueParameter : AResource { }

    public class PackageParameter : AResource {

        //--- Properties ---
        public string DestinationBucketParameterName { get; set; }
        public string DestinationKeyPrefix { get; set; }
        public string SourceFilepath { get; set; }
        public string PackagePath { get; set; }
    }

    public class HumidifierParameter : AResource {

        //--- Properties ---
        public Humidifier.Resource Resource { get; set; }
        public IList<string> DependsOn { get; set; } = new string[0];
        public string Condition { get; set; }
    }

    public class InputParameter : AResource {

        //--- Properties ---
        public string Section { get; set; }
        public string Label { get; set; }
        public bool IsSecret { get; set; }
        public Humidifier.Parameter Parameter { get; set; }
    }

    public class FunctionParameter : AResource {

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