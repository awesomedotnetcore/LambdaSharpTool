![λ#](LambdaSharp_v2_small.png)

# LambdaSharp Module - Using

The `Using` definition creates a namespace for importing cross-module values. By default, these references are resolved by CloudFormation at deployment time. However, they can also be redirected to a different module or be given a specific value instead. This capability allows for a default behavior that is mostly convenient, while enabling modules to be re-wired to import values from other modules, or to be given specific values for testing or legacy purposes.

__Topics__
* [Syntax](#syntax)
* [Properties](#properties)
* [Examples](#examples)

## Syntax

```yaml
Using: String
Description: String
Source: String
Items:
  - ImportDefinition
```

## Properties

<dl>

<dt><code>Description</code></dt>
<dd>
The <code>Description</code> attribute specifies the section description for the imported cross-module values. When omitted, defaults to <i>"${Using} cross-module references"</i>.

<i>Required</i>: No

<i>Type</i>: String
</dd>

<dt><code>Items</code></dt>
<dd>
The <code>Items</code> section specifies the imported values for the referenced module.

<i>Required:</i> Yes

<i>Type:</i> List of [Import Definition](Module-Using-Import.md)
</dd>

<dt><code>Source</code></dt>
<dd>
The <code>Source</code> attribute specifies the full name of the requested module, its version, and origin. The format of the source must be <code>Owner.Name</code>.

<i>Required</i>: Yes

<i>Type</i>: String
</dd>

<dt><code>Using</code></dt>
<dd>
The <code>Using</code> attribute specifies the namespace for imported cross-module values. The name must start with a letter and followed only by letters or digits. Punctuation marks are not allowed. All names are case-sensitive.

<i>Required</i>: Yes

<i>Type</i>: String
</dd>

</dl>

## Examples

### Import a module

```yaml
- Using: MyModuleImports
  Source: My.OtherModule
  Items:

    - Import: MessageTitle
      Description: Imported title for messages
      Type: String

- Variable: ImportedMessageTitle
  Value: !Ref MyModuleImports::MessageTitle
```

### Import a module output and associate IAM permissions

```yaml
- Using: MyModuleImports
  Source: My.OtherModule
  Description: Topic ARN
  Items:

    - Import: Topic
      Description: Topic ARN for sending notifications
      Type: AWS::SNS::Topic
      Allow: Publish

- Variable: ImportedTopic
  Value: !Ref MyModuleImports::Topic
```
