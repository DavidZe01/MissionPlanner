Public keys only (no private keys), for public signing: they give these
assemblies the identities that binaries built for .NET Framework and .NET
reference, so those binaries bind to them.

- ecma.pub: System.Windows.Forms (token b77a5c561934e089)
- open.pub: System.Drawing.Common (token cc7b13ffcd2ddd51)
- microsoft.pub: the System.Drawing facade (token b03f5f7f11d50a3a)

Extracted from Mono's System.Windows.Forms.dll and System.Drawing.dll and from
the System.Drawing.Common NuGet package.
