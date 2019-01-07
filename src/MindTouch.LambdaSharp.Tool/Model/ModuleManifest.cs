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
using YamlDotNet.Serialization;

namespace MindTouch.LambdaSharp.Tool.Model {

    public class ModuleManifest {

        //--- Constants ---
        public const string CurrentVersion = "2018-12-31";

        //--- Properties ---
        public string Version { get; set; } = CurrentVersion;
        public string ModuleInfo { get; set; }
        public IList<ModuleManifestParameterSection> ParameterSections { get; set; }
        public bool RuntimeCheck { get; set; }
        public string Hash { get; set; }
        public string GitSha { get; set; }
        public IList<string> Assets { get; set; }
        public IList<ModuleManifestDependency> Dependencies { get; set; }
        public IDictionary<string, ModuleManifestCustomResource> CustomResourceTypes { get; set; }
        public IList<string> MacroNames { get; set; }
        public IDictionary<string, string> ResourceNameMappings { get; set; }
        public IDictionary<string, string> ResourceTypeNameMappings { get; set; }

        //--- Methods ---
        public string GetFullName() {
            if(!ModuleInfo.TryParseModuleInfo(
                out string moduleOwner,
                out string moduleName,
                out VersionInfo _,
                out string _
            )) {
                throw new ApplicationException("invalid module info");
            }
            return $"{moduleOwner}.{moduleName}";
        }

        public string GetVersion() {
            if(!ModuleInfo.TryParseModuleInfo(
                out string _,
                out string _,
                out VersionInfo moduleVersion,
                out string _
            )) {
                throw new ApplicationException("invalid module info");
            }
            return moduleVersion.ToString();
        }
    }

    public class ModuleManifestCustomResource {

       //--- Properties ---
       public IEnumerable<ModuleManifestResourceProperty> Request { get; set; }
       public IEnumerable<ModuleManifestResourceProperty> Response { get; set; }
    }

    public class ModuleManifestResourceProperty {

       //--- Properties ---
       public string Name { get; set; }
       public string Type { get; set; } = "String";
    }

    public class ModuleManifestDependency {

        //--- Properties ---
        public string ModuleFullName { get; set; }
        public VersionInfo MinVersion { get; set; }
        public VersionInfo MaxVersion { get; set; }
        public string BucketName { get; set; }
    }

    public class ModuleManifestParameterSection {

        //--- Properties ---
        public string Title { get; set; }
        public IList<ModuleManifestParameter> Parameters { get; set; }
    }

    public class ModuleManifestParameter {

        //--- Properties ---
        public string Name { get; set; }
        public string Type { get; set; }
        public string Label { get; set; }
        public string Default { get; set; }
    }
}