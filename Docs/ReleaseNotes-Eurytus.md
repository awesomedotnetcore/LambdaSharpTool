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
    * resource `skip-type-validation` pragma
    * new `Import` notation
    * module `Requires` section
    * `Topic` source now supports `Filters` to filter on SNS notifications
    * `Bucket`, `Queue`, `S3`, `DynamoDB`, `Kinesis` sources can now also be expressions
    * added module `Finalizer` function
    * `Type: Secret` parameters/variables can decrypted for resources using `!Ref SecretParameterName::Plaintext`
    * `DeploymentChecksum` parameter

* CLI
    * new module specification for deploying: `ModuleName[:Version][@Bucket]`
    * publish/deploy/init: added `--force-publish` option
    * files packages are not be created when functions are not compiled (both are about building assets) (i.e. dryrun)

* Build Process
    * validate that function entry point exists after compiling assembly
    * comprehensive variable resolution
    * validate custom resource types using module dependencies
    * validate AWS resources using the cloudformation json spec
    * validation of attribute in `!GetAtt` expressions

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
