using BenchmarkDotNet.Attributes;
using Namotion.Interceptor.Dynamic;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Benchmark;

[MemoryDiagnoser]
public class DynamicSubjectBenchmark
{
    private IInterceptorSubjectContext? _context;
    private IInterceptorSubjectContext? _iterationContext;
    private IMotor? _motor;
    private int _writeCounter;

    public interface IMotor
    {
        int Speed { get; set; }
    }

    public interface ISensor
    {
        int Temperature { get; set; }
    }

    [GlobalSetup]
    public void Setup()
    {
        _context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();
        
        var motor = DynamicSubjectFactory.CreateDynamicSubject(typeof(IMotor), typeof(ISensor));

        // Provisional, matching the anchor a context-taking constructor creates, which is the
        // shape the comparison base measures.
        motor.AttachToContext(_context, SubjectAttachmentAnchorKind.Provisional);
        _motor = (IMotor)motor;
    }
    
    // IterationSetup is intentionally not applied: it forces InvocationCount=1, which puts the
    // nanosecond-scale read and write rows below timer resolution. Only CreateDynamicSubject
    // needs a fresh context per iteration, and it stays disabled.
    public void IterationSetup()
    {
        _iterationContext = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();
    }
    
    //[Benchmark]
    public void CreateDynamicSubject()
    {        
        var subject = DynamicSubjectFactory.CreateDynamicSubject(typeof(IMotor), typeof(ISensor));
        subject.AttachToContext(_iterationContext!, SubjectAttachmentAnchorKind.Provisional);
    }
    
    [Benchmark]
    public void ReadDynamicProperty()
    {
        _ = _motor!.Speed;
    }
    
    [Benchmark]
    public void WriteDynamicProperty()
    {
        // A changing value on every invocation. Writing a constant lands equal to the stored
        // value from the second call onward, and WithFullPropertyTracking installs the equality
        // check, so the row would measure a suppressed write rather than a real one.
        _motor!.Speed = ++_writeCounter;
    }
}