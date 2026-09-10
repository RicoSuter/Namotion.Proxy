using System.Collections;

namespace Namotion.Interceptor.Tracking.Tests.Models;

/// <summary>
/// An empty structural collection that runs a hook the first time it is enumerated, so a test can
/// park inside the discovery scan and act while the attach is halfway through it.
/// </summary>
public sealed class ParkingEnumerable(Action onFirstEnumeration) : IEnumerable<Person>
{
    private int _enumerations;

    public IEnumerator<Person> GetEnumerator()
    {
        if (Interlocked.Increment(ref _enumerations) == 1)
        {
            onFirstEnumeration();
        }

        return Enumerable.Empty<Person>().GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
