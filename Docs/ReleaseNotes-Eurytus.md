# λ# - Eurytus (v0.5) - 2018-11-28

> Eurytus was an eminent Pythagorean philosopher. He was a disciple of Philolaus, and Diogenes Laërtius mentions him among the teachers of Plato, though this statement is very doubtful. [(Wikipedia)](https://en.wikipedia.org/wiki/Eurytus_(Pythagorean))

## What's New

The objective of the λ# 0.5 _Eurytus_ release has been to streamline the iterative development process with the λ# CLI. The biggest time sink for deploying CloudFormation templates is that errors are only detected during the _deploy_ phase. Consequently, the biggest time optimization is to detect errors as early as possible. This pushes the λ# CLI into the validating compiler territory, which has always been the objective. With this release, the λ# CLI validates all properties and attributes for all AWS types. That means, if you forget to set a required property on a resource, you have a typo in the type name or an attribute, the λ# CLI will detect it before attempting to deploy the CloudFormation template.

However, streamlining error detection on AWS types is not enough to really enhance productivity. It also needs to apply to new code in published modules. To that end, the λ# manifest has been enhanced to capture significantly mores information about custom resource types. During the _build_ phase, the λ# CLI downloads the manifest of dependent λ# modules to validate properties and attributes of custom resource types.

> TODO
Finally, λ# modules now have several capabilities that are FREAKISHLY AWESOME! `decrypt` and `finalizer`

__Topics__
1. [Breaking Changes](#breaking-changes)
1. [New λ# Module Features](#new-λ-module-features)
1. [New λ# CLI Features](#new-λ-cli-features)
1. [New λ# Core Features](#new-λ-core-features)
1. [New λ# Assembly Features](#new-λ-assembly-features)
1. [Internal Changes](#internal-changes)


## BREAKING CHANGES

The following change may impact modules created with previous releases.

### Module Definition

* The top-level `Module` attribute must now contain at least one period (`.`) to denote the _module owner_ and _module name_.
* The `Inputs`, `Outputs`, `Variables`, and `Functions` sections of λ# module have been combined into a single `Items` section to give more organizational freedom.
* The `Export` definitions have been removed in favor of a special class for `Scope` on items that can be exported.
* Cross-module references are now grouped into `Using` definitions by module they import from.
* `Package` definitions now compress local files into a deployed zip package only. The zip package can then be used to deploy files to S3--using the `LambdaSharp::S3::Unzip` resource--, create [Lambda Layers](../Samples/LambdaLayerSample), Alexa skills, or anything else that requires a compressed zip package as its input.
* The `Var` definition has been split into two distinct definitions: `Variable` for fixed values and expressions, and `Resource` for existing or new resources.
* The `CustomResource` definition has been renamed to `ResourceType` and enhanced with a specification about its properties and attributes to enable validation at compilation time.
* `Function` definitions lost their `VPC` section and `ReservedConcurrency` attribute. Instead, like `Resource` definitions, they have a `Properties` section that can be used to set these, and other settings, directly. This approach is much more future proof.
* Nesting items has been limited to `Using` and `Namespace` definitions.
* The default value for the `Version` attribute is now `1.0-DEV`.

### λ# Runtime

* The _λ# Runtime_ has been renamed to _λ# Core_. The previous terminology caused too much confusion with the AWS Lambda runtime.
* The λ# Core is deployed as a single module without using nested modules. This decreases the deployment time.

### λ# CLI

* Build process
    * Renamed `--skip-assembly-validation` option to `--no-assembly-validation`
    * Renamed `--cf-output` option to `--cfn-output`
* Publish process
    * Modules are now published to a new location in the S3 deployment bucket: `${Module::Owner}/Modules/${Module::Name}/Versions/${Module::Version}`
    * The λ# manifest is now embedded in the CloudFormation template inside the `Metadata` section under `LambdaSharp::Manifest`.
* Deploy Process
    * Renamed `--inputs` option to `--parameters` for consistency reasons.
    * Removed `--input` option.


### λ# Assemblies

* Assemblies
    * Merged `MindTouch.LambdaSharp.CustomResource` assembly into the `MindTouch.LambdaSharp` assembly.
    * Renamed `MindTouch.LambdaSharp` assembly to `LambdaSharp`.
    * Renamed `MindTouch.LambdaSharp.Slack` assembly to `LambdaSharp.Slack`.
    * Renamed `MindTouch.LambdaSharp.Tool` assembly to `LambdaSharp.Tool`.
* Namespaces and Classes
    * Renamed the `MindTouch.LambdaSharp` namespace to `LambdaSharp`.
    * Renamed `ALambdaEventFunction` to `ALambdaTopicFunction`.



## New λ# Module Features

### Module Owner

The `Module` attribute now specifies both the module owner and the module name. The module owner should be the name of the organization, or individual, to whom the module belongs to. The module owner is used to group related modules in the deployment S3 bucket. The module owner is also a required concept for future compatibility with the [Serverless Application Repository](https://aws.amazon.com/serverless/serverlessrepo/). Everything after the first period is considered to be part of the module name, including additional periods.

In the following example, `Acme` is the module owner and `Accounting.Reports` is the module name:
```yaml
Module: Acme.Accounting.Reports
```

### Module Dependencies

λ# modules now support the concept of dependencies. Required modules are listed in the `Requires` section of the module definition. Listing a module impacts both the build and deploy phases. During the build phase, the module manifests are imported to help with validation. During the deploy phase, required dependencies are checked for and deployed when missing.

In the following example, the module definition indicates that it has a dependency on `LambdaSharp.S3.IO`.
```yaml
Module: Acme.MyModule
Requires:

  - Module: LambdaSharp.S3.IO
```

### Module Items

All definitions in a λ# module are now unified in the `Items` section. This change allows definitions to be more freely organized. For example, it is now possible to group different definition types (parameters, variables, etc) by their purpose, rather than by their type. Definitions can further be grouped into namespaces to better reflect the organization of the module.

The following example shows how definitions can be placed close to where they are needed or close to other definitions they are related to. This design gives greater flexibility to developers to organize their thoughts, not unlike what is found in most programming languages.
```yaml
Module: Acme.MyModule
Items:

  - Parameter: PersonName
    Type: String

  - Variable: GreetPerson
    Type: String
    Scope: Reporting::Calculator
    Value: !Sub "Hi ${PersonName}"

  - Namespace: Reporting
    Items:

      - Resource: Bucket
        Scope: Reporting::Calculator
        Type: AWS::S3::Bucket

      - Function: Calculator
        Memory: 128
        Timeout: 30

  - Variable: AccountingBucket
    Scope: public
    Value: !GetAtt Reporting::Bucket.Arn
```

### `Secret` Type

> TODO
* `Type: Secret` parameters/variables can decrypted for resources using `!Ref SecretParameterName::Plaintext`

### Module Finalizer

λ# modules can now have a _finalizer_ function that is automatically run after all resources have been created or before any resources are torn down. There are many use-cases for a module finalizer, including:
* Initialize a DynamoDB table with seed data on initial module deployment.
* Send an module availability notification to other services.
* Migrate existing data after a module has been updated to a newer version.
* Delete dynamically created resources when the module is torn down.
* Delete objects from an S3 bucket so it can be deleted when the module is torn down.

A module finalizer function must be called `Finalizer` and must appear in the top namespace of the module. The module finalizer timeout is always set to the maximum duration of 15 minutes. On invocation, the module finalizer receives the module version to allow it to track upgrades or downgrade scenarios. It also receives the module checksum so it track state for each CloudFormation update.

The following example shows how easy it is to define a module finalizer:
```yaml
- Function: Finalizer
  Memory: 128
```

### Function `Properties` Section

The `Function` definition now exposes its `Properties` section to access advanced functionality of Lambda functions, such as VPC configuration and Lambda layers. Similar to `Resource` definitions, the function `Properties` are validated during compilation.

The following example shows how to configure a Lambda function for VPC:
```yaml
- Function: MyFunction
  Memory: 128
  Timeout: 30
  Properties:
    VpcConfig:
      SecurityGroupIds: !Ref SecurityGroupIds
      SubnetIds: !Ref SubnetIds
```

The next example shows how to set Lambda layers for a function:
```yaml
- Function: MyFunction
  Memory: 128
  Timeout: 30
  Properties:
    Layers:
      - !Ref MyLambdaLayer
```

### Nested Modules

Nested modules are similar to nested CloudFormation stacks. The module reference is resolved at compile time to a CloudFormation template location. Furthermore, the λ# CLI seamlessly injects the deployment tier parameters required for deploying modules. [See `Nested` documentation.](Module-Nested.md)

The following example shows how to create a nested module definition and access its output values:
```yaml
- Nested: MyNestedModule
  Module: Acme.MyOtherModule:1.0
  Parameters:
    Message: !Sub "Hi from ${Module::Name}"

- Variable: NestedOutput
  Value: !Ref MyNestedModule::OutputName
```

### `Condition` Item

λ# modules now support `Condition` items and the corresponding `!Condition` function. The `If` attribute is used on `Resource` and `Function` items to indicate that they are conditional.

The following example shows a `Condition` item keying off a `Parameter` and thus controlling the creation of two conditional resources.
```yaml
- Parameter: EnvType
  Description: Environment type.
  Default: test
  Type: String
  AllowedValues:
    - prod
    - test
  ConstraintDescription: must specify prod or test.

- Resource: EC2Instance
  Type: "AWS::EC2::Instance"
  Properties:
    ImageId: ami-0ff8a91507f77f867

- Namespace: ProductionResources
  Items:

    - Condition: Create
      Value: !Equals [ !Ref EnvType, prod ]

    - Resource: MountPoint
      Type: AWS::EC2::VolumeAttachment
      If: ProductionResources::Create
      Properties:
        InstanceId: !Ref EC2Instance
        VolumeId: !Ref ProductionResources::NewVolume
        Device: /dev/sdh

    - Resource: NewVolume
      Type: AWS::EC2::Volume
      If: ProductionResources::Create
      Properties:
        Size: 100
        AvailabilityZone: !GetAtt EC2Instance.AvailabilityZone
```

The `If` attribute can either have the name of a `Condition` item or contain the conditional expression directly. The following two examples are identical:
```yaml
- Condition: Create
  Value: !Equals [ !Ref CreateTopic, yes ]

- Resource: ConditionalTopic
  Type: AWS::SNS::Topic
  If: ProductionResources::Create
```
-VS-
```yaml
- Resource: ConditionalTopic
  Type: AWS::SNS::Topic
  If: !Equals [ !Ref CreateTopic, yes ]
```

Conditional resources that also have a `Scope` attribute are conditionally injected into their specified Lambda functions. Care needs to be taken when reading them from the function configuration to allow for them to be missing.

### `Mapping` Item

λ# modules now also support `Mapping` items and the corresponding `!FindInMap` function.

The next example shows a definition of a `Mapping` item and its use:
```yaml
- Mapping: Greetings
  Description: Time of day greeting
  Value:
    Morning:
      Text: Good morning
    Day:
      Text: Good day
    Evening:
      Text: Good evening
    Night:
      Text: Good night

- Parameter: SelectedTime
  Description: Parameter for selecting the time of day
  AllowedValues:
    - Morning
    - Day
    - Evening
    - Night

- Variable: SelectedGreeting
  Description: Selected greeting
  Value: !FindInMap [ Greetings, !Ref SelectedTime, Text ]
```

### Function Sources
* `DynamoDB` can now be a `!Ref` expression.
* `Kinesis` can now be a `!Ref` expression.
* `Queue` can now be a `!Ref` expression.
* `S3` can now be a `!Ref` expression.
* `Schedule` can now be a `!Ref` expression.
* `Topic` can now be a `!Ref` expression.
* `Topic` source now supports `Filters` to filter on SNS notifications.

> TODO


## New λ# CLI Features

* garbage collection of optional resources and conditions
    * issue warning if a `Parameter` is never used (but don't garbage collect it!)
* updated manifest format, includes: resource types, macros, and outputs
* include `git` branch information in manifest and lambda function
* new module specification for deploying: `ModuleName[:Version][@Bucket]`
* publish/deploy/init: added `--force-publish` option
* files packages are not be created when functions are not compiled (both are about building assets) (i.e. dryrun)
* `config` command
    * now has the option to set a specific bucket name
    * set bucket policy to allow serverless-repo to access the contents
    * prompt for parameters when missing (computes delta of old and new cloudformation template)
* `--no-dependency-validation` to disable downloading of dependencies
* added `util delete-orphan-lambda-logs` command to delete orphaned Lambda log groups
* expose `util` commands
* added `--git-branch` option
* validate that function entry point exists after compiling assembly
* comprehensive variable resolution
* validate custom resource types using module dependencies
* validate AWS resource names using the cloudformation json spec
* validation of attribute in `!GetAtt` expressions
* `ModuleCloudWatchLogsRole` is defined once in base module and then re-used by all modules
* garbage collection generated import parameters if not used
* warn on unused parameters
* simplify references in `!Sub` expressions
* validate that `!Ref` and `!GetAtt` references to conditional resources are only made from compatible, conditional resources
* Deploy Process
    * use change-sets for deploying stacks
    * translate custom resource types from `Custom::LambdaSharpRegisterFunction` to `LambdaSharp::Register::Function` when showing the stack update (also resource names)
    * before deploying a module/dependency, prompt for any missing parameters
    * option `--prompt-all` prompts for all missing parameters, including those with default values
    * option `--prompts-as-errors` causes any prompt to be reported as an error instead
* show time and date when command finished
* renamed `--cf-output` option to `--cfn-output`; output can now be a path, in which case the module source name is used as output json
* Publish process
    * prevent re-publishing the same version unless the version has as suffix (i.e. pre-release)



## New λ# Core Features

* added `LambdaSharp::S3::WriteJson` resource type
* custom resource `LambdaSharp::S3::WriteJson`
* custom resource `LambdaSharp::S3::EmptyBucket`
* custom resource `LambdaSharp::S3::Unzip`
```yaml
- Resource: WriteConfigFile
    Type: LambdaSharp::S3::WriteFile
    Properties:
    Bucket: !Ref Website::Bucket
    Key: config.json
    Contents:
        api:
        invokeUrl: !Ref Api::DomainName
        scheme: https
```
* LambdaSharp.S3.IO
    * `LambdaSharp::S3::WriteJson`
    * `LambdaSharp::S3::EmptyBucket`
    * `LambdaSharp::S3::Unzip` only upload changed files



## New λ# Assembly Features

* added `ALambdaFinalizerFunction` base class
* `ALambdaCustomResourceFunction` can be invoked via SNS or directly from a custom resource



## Internal Changes

> TODO
* default log retention was increased from 7 to 30 days



