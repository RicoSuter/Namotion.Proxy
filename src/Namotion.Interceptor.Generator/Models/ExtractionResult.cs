using System.Collections.Generic;
using Microsoft.CodeAnalysis;

namespace Namotion.Interceptor.Generator.Models;

/// <summary>
/// The outcome of inspecting one candidate subject type. A null <see cref="Metadata"/> means no
/// source should be emitted, which is how a suppressing diagnostic prevents a cascade of
/// consequent compiler errors.
/// </summary>
internal sealed record ExtractionResult(
    SubjectMetadata? Metadata,
    IReadOnlyList<Diagnostic> Diagnostics);
