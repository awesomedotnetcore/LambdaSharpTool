# λ# - Eurytus (v0.5) - 2018-11-28

> Eurytus was an eminent Pythagorean philosopher. He was a disciple of Philolaus, and Diogenes Laërtius mentions him among the teachers of Plato, though this statement is very doubtful. [(Wikipedia)](https://en.wikipedia.org/wiki/Eurytus_(Pythagorean))

## What's New

> TODO

* Module
    * generalized input/variable/function/output node types using `Entries:` section
    * **BREAKING CHANGE:** new module entries notation
        * `Resource`: AWS and custom resources
        * `Variable`: hold arbitrary literal or intermediate values
        * `Package`: file package only (**BREAKING CHANGE:** files are no longer always deployed to S3)
        * `Module`: create nested module resource
    * allow any AWS type as parameter type and map to `String` when AWS type is not natively supported
    * new `Import` notation
    * module `Requires` section
    * `Topic` source now supports `Filters` to filter on SNS notifications
    * `Bucket`, `Queue`, `S3`, `DynamoDB`, `Kinesis` sources can now also be expressions
    * added module `Finalizer` function
        * `Finalizer` is sent two properties: `DeploymentChecksum` to detect changes and `ModuleVersion` to detect upgrade/downgrade scenarios
    * `Type: Secret` parameters/variables can decrypted for resources using `!Ref SecretParameterName::Plaintext`
    * `DeploymentChecksum` parameter
    * Pragmas
        * Resources
            * `no-type-validation`: don't validate attributes on resource
        * Functions
            * `no-assembly-validation`: don't validate that the λ# assemblies referenced by the .csproj file are consistent with the CLI version
            * `no-handler-validation`: don't validate if the lambda function handler can be found in the compiled assembly
            * `no-function-registration`: don't register function with λ# registrar
            * `no-dead-letter-queue`: don't add the DLQ to the function
        * Modules
            * `no-runtime-version-check`: don't check if the λ# runtime and CLI versions match
            * `no-module-registration`: don't register module with λ# registrar
            * `no-lambdasharp-dependencies`: don't reference λ# base resources (DLQ, Logging Stream, etc.)
    * default log retention was increased from 7 to 30 days
    * added support for `Condition` entry
    * garbage collection of optional resources and conditions
    * entry type `Condition`
        * allow custom condition in resources and functions
        * add support for `!Condition`
        * add tests

* CLI
    * new module specification for deploying: `ModuleName[:Version][@Bucket]`
    * publish/deploy/init: added `--force-publish` option
    * files packages are not be created when functions are not compiled (both are about building assets) (i.e. dryrun)
    * `config` command
        * now has the option to set a specific bucket name
        * set bucket policy to allow serverless-repo to access the contents
        * prompt for parameters when missing (computes delta of old and new cloudformation template)
    * BREAKING: `--skip-assembly-validation` is now `--no-assembly-validation` for consistency reasons
    * `--no-dependency-validation` to disable downloading of dependencies

* Build Process
    * validate that function entry point exists after compiling assembly
    * comprehensive variable resolution
    * validate custom resource types using module dependencies
    * validate AWS resources using the cloudformation json spec
    * validation of attribute in `!GetAtt` expressions
    * `ModuleCloudWatchLogsRole` is defined once in base module and then re-used by all modules
    * garbage collection generated import parameters if not used

* Deploy Process
    * use change-sets for deploying stacks
    * translate custom resource types from `Custom::LambdaSharpRegisterFunction` to `LambdaSharp::Register::Function` when showing the stack update (also resource names)
    * λ# manifest embedded in cloudformation template

* `MindTouch.LambdaSharp` assembly
    * added `ALambdaFinalizerFunction` base class
    * **BREAKING CHANGE:** merged `ALambdaCustomResourceFunction` into LambdaSharp assembly
    * `ALambdaCustomResourceFunction` can be invoked via SNS or directly from a custom resource


__Topics__
1. [Breaking Changes](#breaking-changes)
1. [New λ# CLI Features](#new-λ-cli-features)
1. [New λ# Module Features](#new-λ-module-features)
1. [New λ# Runtime Features](#new-λ-runtime-features)
1. [New λ# Assembly Features](#new-λ-assembly-features)
1. [Internal Changes](#internal-changes)


## BREAKING CHANGES

The following change may impact modules created with previous releases.

### Module Definition

> TODO

### λ# Tool

> TODO

### λ# Assemblies

> TODO


## New λ# CLI Features

> TODO

## New λ# Module Features

> TODO

## New λ# Runtime Features

> TODO

## New λ# Assembly Features

> TODO


## Internal Changes

> TODO

## Fixes

> TODO
