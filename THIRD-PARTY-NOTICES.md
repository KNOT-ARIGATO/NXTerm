# Third-party notices

NXTerm.exe includes the following open-source components. Each of them is
distributed under the MIT License; the copyright notices are listed below,
followed by the license text that applies to every component in this file.

## Components

### SSH.NET 2026.0.0

- Project: https://github.com/sshnet/SSH.NET
- Copyright (c) Renci 2010-2026

### BouncyCastle.Cryptography 2.7.0 (used by SSH.NET)

- Project: https://www.bouncycastle.org/csharp/
- Copyright (c) 2000-2026 The Legion of the Bouncy Castle Inc. (https://www.bouncycastle.org)

### System.IO.Ports 10.0.12 and System.Management 10.0.12

- Project: https://github.com/dotnet/runtime
- Copyright (c) .NET Foundation and Contributors
- These libraries carry their own third-party notices:
  https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT

### Microsoft.Extensions.Logging.Abstractions 8.0.3 and Microsoft.Extensions.DependencyInjection.Abstractions 8.0.2 (used by SSH.NET)

- Project: https://github.com/dotnet/runtime
- Copyright (c) .NET Foundation and Contributors

## MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

## Root certificates

NXTerm.exe carries the public root certificates of Sectigo (including
USERTrust), ISRG (Let's Encrypt) and DigiCert, exactly as those certificate
authorities publish them. The update check uses them to verify GitHub's
servers on PCs whose Windows root certificates are out of date.

## Trademarks

NXTerm is an independent project written from scratch. All product names
mentioned in this software and its documentation are the property of their
respective owners.
