![λ#](Docs/LambdaSharp_v2_small.png)

# LambdaSharp CLI & Framework (v0.5)

**[Read what's new in the 0.5 "Eurytus" release.](Docs/ReleaseNotes-Eurytus.md)**

λ# is a CLI and a framework for rapid serverless application development. λ# uses a simple declarative syntax to generate sophisticated CloudFormation templates that provide simple, yet flexible, deployment options.

The objective of λ# is to accelerate the innovation velocity of serverless solutions. It allows developers to focus on solving business problems while deploying scalable, observable solutions that follow DevOps best practices.

## Prerequisite

1. [AWS CLI](https://docs.aws.amazon.com/cli/latest/userguide/cli-chap-getting-started.html)
1. [.NET Core 2.1+](https://www.microsoft.com/net/download)

## Getting Started

The λ# CLI is installed as a .NET Global Tool.

```bash
dotnet tool install -g LambdaSharp.Tool
```

Once installed, the λ# CLI needs to be configured.
```bash
dotnet lash config
```

Finally, a deployment tier must be initialized with the λ# Core.
```bash
dotnet lash init --tier Sandbox
```

## Deploy your First λ# Module

Creating modules with Lambda functions and deploying them only requires a few steps.

```bash
# Create a new λ# module
dotnet lash new module MySampleModule

# Add a function to the λ# module
dotnet lash new function MyFunction

# Deploy the λ# module
dotnet lash deploy
```

The λ# CLI uses a YAML file to compile the C# projects, upload assets, and deploy the CloudFormation stack in one step. The YAML file describes the entire module including the inputs, outputs, variables, resources, and functions.

```yaml
Module: MySampleModule
Items:

 - Function: MyFunction
   Memory: 128
   Timeout: 30
```

The C# project contains the Lambda handler.

```csharp
namespace MySampleModule.MyFunction {

    public class FunctionRequest {

        // add request fields
    }

    public class FunctionResponse {

        // add response fields
    }

    public class Function : ALambdaFunction<FunctionRequest, FunctionResponse> {

        //--- Methods ---
        public override Task InitializeAsync(LambdaConfig config)
            => Task.CompletedTask;

        public override async Task<FunctionResponse> ProcessMessageAsync(FunctionRequest request, ILambdaContext context) {

            // add business logic

            return new FunctionResponse();
        }
    }
}
```

## Learn More

1. [Setup λ#](Docs/)
1. [λ# CLI Reference](src/LambdaSharp.Tool/)
1. [λ# Samples](Samples/)
1. [Module Definition Reference](Docs/Module.md)
1. [Folder Structure Reference](Docs/FolderStructure.md)
1. [Release Notes](Docs/ReadMe.md)
1. [Module IAM Short-hands](src/LambdaSharp.Tool/Resources/IAM-Mappings.yml)

## License

> Copyright (c) 2018,2019 MindTouch
>
> Licensed under the Apache License, Version 2.0 (the "License");
> you may not use this file except in compliance with the License.
> You may obtain a copy of the License at
>
> http://www.apache.org/licenses/LICENSE-2.0
>
> Unless required by applicable law or agreed to in writing, software
> distributed under the License is distributed on an "AS IS" BASIS,
> WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
> See the License for the specific language governing permissions and
> limitations under the License.
