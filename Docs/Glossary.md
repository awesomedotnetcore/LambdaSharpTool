![λ#](LambdaSharp_v2_small.png)

# LambdaSharp Glossary

> TODO: WIP for capturing λ# terminology

<dl>

<dt><b>Asset</b></dt>
<dd>A file that is part of the published module.</dd>

<dt><b>Attribute</b></dt>
<dd>A YAML mapping for a single value or list of values.</dd>

<dt><b>Build Process</b></dt>
<dd>
The process by which the λ# CLI converts the source YAML module file into a CloudFormation JSON template file. The contents of the source are analyzed during the build process to detect errors, such as missing properties required to initialize a resource or references to undefined variables.
</dd>

<dt><b>Core</b></dt>
<dd>The foundational CloudFormation template required to deploy and run modules.</dd>

<dt><b>Cross-Module Reference</b></dt>
<dd>
A value imported from another module using <code>!ImportValue</code> where the source module is configurable. Optionally, the source module can also be replaced with a fixed value.
</dd>

<dt><b>Deployment Process</b></dt>
<dd>
The process by which the λ# CLI creates a CloudFormation stack from a CloudFormation template that was created from a λ# module. The deployment process checks for dependencies and installs them if needed. During the deployment process, the λ# CLI uses interactive prompts for obtain values for missing parameters. In addition, the λ# CLI supplies required parameters, such as the deployment bucket name and deployment tier prefix to launch the CloudFormation stack.
</dd>

<dt><b>Deployment Tier</b></dt>
<dd>
> TODO
</dd>

<dt><b>Import</b></dt>
<dd>(see <i>Cross-Module Reference</i>)</dd>

<dt><b>Module Definition</b></dt>
<dd>
> TODO
</dd>

<dt><b>Package</b></dt>
<dd>A package is a compressed zip archive of files.</dd>

<dt><b>Publishing Process</b></dt>
<dd>
> TODO
</dd>

<dt><b>Section</b></dt>
<dd>A YAML mapping for another YAML mapping.</dd>

<dt><b>Tier</b></dt>
<dd>(see <i>Deployment Tier</i>)</dd>


<!-- <dt><b></b></dt>
<dd>
> TODO
</dd> -->


</dl>
