// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using Linkast.Core;
using Serilog.Events;
using Serilog.Formatting;

namespace Linkast.App.Logging;

/// <summary>Renders with an inner formatter, then removes secrets before anything reaches disk.</summary>
public sealed class RedactingTextFormatter(ITextFormatter inner, Redactor redactor) : ITextFormatter
{
    public void Format(LogEvent logEvent, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(output);
        using var buffer = new StringWriter();
        inner.Format(logEvent, buffer);
        output.Write(redactor.Redact(buffer.ToString()));
    }
}
