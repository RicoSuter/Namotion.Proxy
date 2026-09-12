; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|------
NI0001 | Namotion.Interceptor | Error | Interceptor subject must be partial
NI0002 | Namotion.Interceptor | Error | Containing type of an interceptor subject must be partial
NI0003 | Namotion.Interceptor | Error | InterceptorSubject is only supported on classes
NI0004 | Namotion.Interceptor | Error | Interceptor subject generation failed
NI0005 | Namotion.Interceptor | Error | Generic interceptor subjects are not supported
NI0006 | Namotion.Interceptor | Error | File-local interceptor subjects are not supported
NI0007 | Namotion.Interceptor | Error | Base class does not satisfy the subject base contract
NI0020 | Namotion.Interceptor | Warning | Attributes on an explicit interface implementation are ignored
NI0040 | Namotion.Interceptor | Warning | Unsupported member skipped
NI0060 | Namotion.Interceptor | Error | Property re-declares a member already implemented by the base class
NI0061 | Namotion.Interceptor | Error | More than one member provides the same property name
NI0062 | Namotion.Interceptor | Warning | Base class interception members cannot be shared
NI0063 | Namotion.Interceptor | Error | Member hides an inherited generated member
NI0064 | Namotion.Interceptor | Error | Member hijacks an inherited interface implementation
NI0065 | Namotion.Interceptor | Error | Property displaces a property an ancestor subject already exposes
