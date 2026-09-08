using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Generator.Tests.Models;

[InterceptorSubject]
public partial class PersonBase
{
    public partial string? FirstName { get; set; }
}

[InterceptorSubject]
public partial class Employee : PersonBase
{
    public partial string? Department { get; set; }
}

// The base list deliberately lives on a declaration other than the attributed one: the base class
// has to be resolved from the symbol, not from the attributed declaration's own base list.
[InterceptorSubject]
public partial class Contractor
{
    public partial string? Agency { get; set; }
}

public partial class Contractor : PersonBase
{
}
