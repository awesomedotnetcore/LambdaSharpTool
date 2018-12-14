/*
 * MindTouch λ#
 * Copyright (C) 2006-2018 MindTouch, Inc.
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

using System.Linq;
using System.Text;

namespace MindTouch.LambdaSharp.Tool {

    public class ModuleLocation {

        //--- Properties ---
        public string ModuleName { get; set; }
        public VersionInfo ModuleVersion { get; set; }
        public string BucketName { get; set; }
        public string TemplatePath { get; set; }

        //--- Methods ---
        public override string ToString() {
            var result = new StringBuilder();
            if(ModuleName != null) {
                result.Append(ModuleName);
                if(ModuleVersion != null) {
                    result.Append($" (v{ModuleVersion})");
                }
                result.Append(" from ");
                result.Append(BucketName);
            } else {
                result.Append($"s3://{BucketName}/{TemplatePath}");
            }
            return result.ToString();
        }

        public string ToModuleReference()
            => (BucketName != null)
                ? $"{ModuleName}:{ModuleVersion}@{BucketName}"
                : $"{ModuleName}:{ModuleVersion}";
    }
}