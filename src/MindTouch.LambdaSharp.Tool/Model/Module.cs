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
using System.Linq;
using MindTouch.LambdaSharp.Tool.Internal;
using Newtonsoft.Json;
using YamlDotNet.Serialization;

namespace MindTouch.LambdaSharp.Tool.Model {

    public class ModuleGrant {

        //--- Properties ---
        public string Sid { get; set; }
        public object References { get; set; }
        public IList<string> Allow { get; set; }
    }

    public class Module {

        //--- Fields ---
        [JsonIgnore]
        private readonly Dictionary<string, ModuleEntry> _entriesByFullName = new Dictionary<string, ModuleEntry>();

        [JsonIgnore]
        private readonly List<ModuleEntry> _entries = new List<ModuleEntry>();

        //--- Properties ---
        public string Name { get; set; }
        public VersionInfo Version { get; set; }
        public string Description { get; set; }
        public IList<object> Pragmas { get; } = new List<object>();
        public IList<object> Secrets { get; set; } = new List<object>();
        public IList<AOutput> Outputs { get; } = new List<AOutput>();
        public IDictionary<string, object> Conditions  { get; set; } = new Dictionary<string, object>();
        public List<Humidifier.Statement> ResourceStatements { get; } = new List<Humidifier.Statement>();
        public IList<ModuleGrant> Grants { get; } = new List<ModuleGrant>();
        public IEnumerable<ModuleEntry> Entries => _entries;

        [JsonIgnore]
        public bool HasModuleRegistration => !HasPragma("no-module-registration");

        //--- Methods ---
        public bool HasPragma(string pragma) => Pragmas?.Contains(pragma) == true;
        public ModuleEntry GetEntry(string fullName) => _entriesByFullName[fullName];
        public object GetReference(string fullName) => GetEntry(fullName).Reference;
        public bool TryGetEntry(string fullName, out ModuleEntry entry) => _entriesByFullName.TryGetValue(fullName, out entry);
        public ModuleEntry AddEntry(ModuleEntry entry) {
            _entriesByFullName.Add(entry.FullName, entry);
            _entries.Add(entry);
            return entry;
        }

        public IEnumerable<ModuleEntry> GetAllEntriesOfType<T>()
            => Entries
                .Where(entry => entry.Resource is T)
                .ToList();
    }
}