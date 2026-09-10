using System.ComponentModel;
using Namotion.Interceptor.Generator;

namespace Namotion.Interceptor.Generator.Tests;

/// <summary>
/// Pins the member names the generator carries as string constants against the interfaces that
/// really declare them. The generator itself cannot use <c>nameof</c> for this: it ships as an
/// analyzer and deliberately does not reference <c>Namotion.Interceptor</c>, so none of these types
/// are symbols there. This project does reference it, so the comparison belongs here.
/// </summary>
/// <remarks>
/// Only the names that correspond to a real declared member can be pinned this way. The five helper
/// names and <c>DefaultProperties</c> name members the generator emits into consumer code, so there
/// is nothing to compare them against; the emitter writes their signatures and the compiler checks
/// those, with the snapshot tests covering the rest.
/// </remarks>
public class GeneratedMemberTableTests
{
    [Fact]
    public void WhenTheNotifyMemberNamesAreRead_ThenTheyMatchTheDeclaringInterfaces()
    {
        // Act & Assert
        Assert.Equal(nameof(INotifyPropertyChanged.PropertyChanged), MemberNames.PropertyChanged);
        Assert.Equal(nameof(IRaisePropertyChanged.RaisePropertyChanged), MemberNames.RaisePropertyChanged);
    }

    [Fact]
    public void WhenAnIInterceptorSubjectMemberNameIsTested_ThenTheNameSetClaimsIt()
    {
        // Arrange: the three members NI0014 protects. The array holding them is private, so this reads
        // the name set derived from it. A typo there stops the real interface member name being
        // recognised, which is what fails here.
        // Act & Assert
        Assert.True(GeneratedMemberTable.CollidesWithGeneratedMember(nameof(IInterceptorSubject.Executor)));
        Assert.True(GeneratedMemberTable.CollidesWithGeneratedMember(nameof(IInterceptorSubject.Data)));
        Assert.True(GeneratedMemberTable.CollidesWithGeneratedMember(nameof(IInterceptorSubject.AddProperties)));
    }

    [Fact]
    public void WhenPropertiesIsTested_ThenItIsDeliberatelyAbsentFromTheNameSet()
    {
        // Arrange & Act & Assert: IInterceptorSubject declares four members and the hijack rule covers
        // three. Properties is left out because every subject emits its own explicit implementation of
        // it, which always wins, so covering it would report every legitimate generated hierarchy.
        // Nothing else feeding this name set is called Properties, so its absence here is the
        // exclusion.
        Assert.False(GeneratedMemberTable.CollidesWithGeneratedMember(nameof(IInterceptorSubject.Properties)));
    }
}
