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

    public class ModuleVariable {

        //--- Properties ---
        public string FullName { get; set; }
        public IList<string> Scope { get; set; }
        public object Reference { get; set; }
    }

    public class ModuleGrant {

        //--- Properties ---
        public string Sid { get; set; }
        public object References { get; set; }
        public IList<string> Allow { get; set; }
    }

    public class Module {

        //--- Properties ---
        public string Name { get; set; }
        public VersionInfo Version { get; set; }
        public string Description { get; set; }
        public IList<object> Pragmas { get; } = new List<object>();
        public IList<object> Secrets { get; set; } = new List<object>();
        public IDictionary<string, ModuleVariable> Variables { get; } = new Dictionary<string, ModuleVariable>();
        public IList<AResource> Resources { get; } = new List<AResource>();
        public IList<AOutput> Outputs { get; } = new List<AOutput>();
        public IDictionary<string, object> Conditions  { get; set; } = new Dictionary<string, object>();
        public IList<object> ResourceStatements { get; } = new List<object>();
        public IList<ModuleGrant> Grants { get; } = new List<ModuleGrant>();

        [JsonIgnore]
        public bool HasModuleRegistration => !HasPragma("no-module-registration");

        [JsonIgnore]
        public IEnumerable<Function> Functions => GetAllResources().OfType<Function>();

        //--- Methods ---
        public bool HasPragma(string pragma) => Pragmas?.Contains(pragma) == true;

        public AResource GetResource(string fullName) => Resources.First(resource => resource.FullName == fullName);

        public IEnumerable<AResource> GetAllResources() => Resources;
    }
}