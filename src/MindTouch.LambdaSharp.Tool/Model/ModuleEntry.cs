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

namespace MindTouch.LambdaSharp.Tool.Model {

    public class ModuleEntry {

        //--- Constructors ---
        public ModuleEntry(
            string fullName,
            string description,
            object reference,
            IList<string> scope,
            AResource resource
        ) {
            FullName = fullName ?? throw new ArgumentNullException(nameof(fullName));
            LogicalId = fullName.Replace("::", "");
            ResourceName = "@" + LogicalId;
            Reference = reference;
            Scope = scope ?? new string[0];
            Resource = resource;
        }

        //--- Properties ---
        public string FullName { get; }
        public string Description { get; }
        public string ResourceName { get; }
        public string LogicalId { get; }
        public AResource Resource { get; }
        public object Reference { get; set; }
        public IList<string> Scope { get; set; }
    }
}