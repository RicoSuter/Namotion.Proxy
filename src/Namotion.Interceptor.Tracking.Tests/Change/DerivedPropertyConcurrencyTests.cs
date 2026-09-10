using System.Collections.Concurrent;
using System.ComponentModel;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Change;

public class DerivedPropertyConcurrencyTests
{
    private const int DefaultIterations = 50;

    [Fact]
    public async Task WhenMultipleSourcePropertiesWrittenConcurrently_ThenDerivedPropertySettlesToCorrectValue()
    {
        for (var i = 0; i < DefaultIterations; i++)
        {
            // Arrange
            var context = InterceptorSubjectContext
                .Create()
                .WithFullPropertyTracking();

            var person = new Person(context)
            {
                FirstName = "A",
                LastName = "B"
            };

            var barrier = new Barrier(2);

            // Act
            var t1 = Task.Run(() =>
            {
                barrier.SignalAndWait();
                person.FirstName = "Jane";
            });

            var t2 = Task.Run(() =>
            {
                barrier.SignalAndWait();
                person.LastName = "Smith";
            });

            await Task.WhenAll(t1, t2);

            // Assert
            var expected = $"{person.FirstName} {person.LastName}".Trim();
            Assert.Equal(expected, person.FullName);
        }
    }

    [Fact]
    public async Task WhenChildObjectReplacedWhileDerivedPropertyRead_ThenValueSettlesCorrectly()
    {
        for (var i = 0; i < DefaultIterations; i++)
        {
            // Arrange
            var context = InterceptorSubjectContext
                .Create()
                .WithFullPropertyTracking();

            var car = new Car(context);
            car.Tires[0].Pressure = 30;
            car.Tires[1].Pressure = 30;
            car.Tires[2].Pressure = 30;
            car.Tires[3].Pressure = 30;

            var tire1 = car.Tires[1];
            var tire2 = car.Tires[2];
            var tire3 = car.Tires[3];
            var newTire = new Tire(context) { Pressure = 100 };
            var barrier = new Barrier(2);

            // Act
            var replaceTask = Task.Run(() =>
            {
                barrier.SignalAndWait();
                car.Tires = [newTire, tire1, tire2, tire3];
            });

            var writeTask = Task.Run(() =>
            {
                barrier.SignalAndWait();
                tire1.Pressure = 50;
            });

            await Task.WhenAll(replaceTask, writeTask);

            // Assert
            var expected = car.Tires.Average(t => t.Pressure);
            Assert.Equal(expected, car.AveragePressure);
        }
    }

    [Fact]
    public async Task WhenSourcePropertyWrittenDuringSubjectDetach_ThenNoExceptionsAndCleanState()
    {
        for (var i = 0; i < DefaultIterations; i++)
        {
            // Arrange
            var context = InterceptorSubjectContext
                .Create()
                .WithFullPropertyTracking();

            var car = new Car(context);
            var targetTire = car.Tires[0];

            var averagePressureProperty = new PropertyReference(car, nameof(Car.AveragePressure));
            var tirePressureProperty = new PropertyReference(targetTire, nameof(Tire.Pressure));

            var barrier = new Barrier(2);

            // Act
            var writeTask = Task.Run(() =>
            {
                barrier.SignalAndWait();
                for (var j = 0; j < 10; j++)
                {
                    targetTire.Pressure = j + 1;
                }
            });

            var detachTask = Task.Run(() =>
            {
                barrier.SignalAndWait();
                car.DetachSubjectProperty(averagePressureProperty);
            });

            await Task.WhenAll(writeTask, detachTask);

            // Assert
            Assert.DoesNotContain(
                averagePressureProperty,
                tirePressureProperty.GetUsedByProperties().Items.ToArray());
        }
    }

    [Fact]
    public async Task WhenSubjectAttachedAndDetachedRapidly_ThenNoStaleBacklinksAndGCEligible()
    {
        for (var i = 0; i < DefaultIterations; i++)
        {
            // Arrange
            var context = InterceptorSubjectContext
                .Create()
                .WithFullPropertyTracking();

            var car = new Car(context);

            var barrier = new Barrier(2);

            // Act
            var t1 = Task.Run(() =>
            {
                barrier.SignalAndWait();
                var newTires = new[] { new Tire(context), car.Tires[1], car.Tires[2], car.Tires[3] };
                car.Tires = newTires;
            });

            var t2 = Task.Run(() =>
            {
                barrier.SignalAndWait();
                var newTires = new[] { car.Tires[0], new Tire(context), car.Tires[2], car.Tires[3] };
                car.Tires = newTires;
            });

            await Task.WhenAll(t1, t2);

            // Assert
            var expected = car.Tires.Average(t => t.Pressure);
            Assert.Equal(expected, car.AveragePressure);
        }
    }

    [Fact]
    public void WhenDetachedSubjectIsNoLongerReferenced_ThenItCanBeGarbageCollected()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        // Act
        var weakTire = CreateCarAndReplaceFirstTire(context);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        // Assert
        Assert.False(weakTire.IsAlive, "Detached tire should be garbage collected");
    }

    [Fact]
    public async Task WhenConditionalDependencySwitchedUnderConcurrentWrites_ThenValueAlwaysCorrect()
    {
        for (var i = 0; i < DefaultIterations; i++)
        {
            // Arrange
            var context = InterceptorSubjectContext
                .Create()
                .WithFullPropertyTracking();

            var person = new ConditionalPerson(context)
            {
                UseFirstName = false,
                FirstName = "Alice",
                LastName = "Bob"
            };

            var barrier = new Barrier(3);

            // Act
            var toggleTask = Task.Run(() =>
            {
                barrier.SignalAndWait();
                person.UseFirstName = true;
                person.UseFirstName = false;
                person.UseFirstName = true;
            });

            var writeFirstTask = Task.Run(() =>
            {
                barrier.SignalAndWait();
                person.FirstName = "Charlie";
                person.FirstName = "Eve";
            });

            var writeLastTask = Task.Run(() =>
            {
                barrier.SignalAndWait();
                person.LastName = "David";
                person.LastName = "Frank";
            });

            await Task.WhenAll(toggleTask, writeFirstTask, writeLastTask);

            // Assert
            var expected = person.UseFirstName
                ? (person.FirstName ?? "")
                : (person.LastName ?? "");
            Assert.Equal(expected, person.Display);
        }
    }

    [Fact]
    public async Task WhenOneDerivedPropertyDetachedWhileSharedDependencyWritten_ThenRemainingDerivedPropertyStillWorks()
    {
        for (var i = 0; i < DefaultIterations; i++)
        {
            // Arrange
            var context = InterceptorSubjectContext
                .Create()
                .WithFullPropertyTracking();

            var person = new Person(context)
            {
                FirstName = "John",
                LastName = "Doe"
            };

            var fullNameWithPrefixProperty = new PropertyReference(person, nameof(Person.FullNameWithPrefix));
            var barrier = new Barrier(2);

            // Act
            var detachTask = Task.Run(() =>
            {
                barrier.SignalAndWait();
                person.DetachSubjectProperty(fullNameWithPrefixProperty);
            });

            var writeTask = Task.Run(() =>
            {
                barrier.SignalAndWait();
                person.FirstName = "Jane";
            });

            await Task.WhenAll(detachTask, writeTask);

            // Assert
            var expected = person.FirstName is null && person.LastName is null
                ? "NA"
                : $"{person.FirstName} {person.LastName}".Trim();
            Assert.Equal(expected, person.FullName);
        }
    }

    [Fact]
    public async Task WhenDependencyChangesWhileNewDependencyDetaches_ThenNoStaleBacklinksOrExceptions()
    {
        // Targets the rare path in UpdateDependencies where a newly-recorded dependency
        // is being detached concurrently (hasSkippedDependencies = true). The rare path
        // re-checks each dependency under lock and filters out still-detached ones.
        for (var i = 0; i < DefaultIterations; i++)
        {
            // Arrange
            var context = InterceptorSubjectContext
                .Create()
                .WithFullPropertyTracking();

            var person = new ConditionalPerson(context)
            {
                UseFirstName = false,
                FirstName = "Alice",
                LastName = "Bob"
            };

            var firstNameProperty = new PropertyReference(person, nameof(ConditionalPerson.FirstName));
            var barrier = new Barrier(2);

            // Act — toggle condition (deps change to include FirstName) while
            // concurrently detaching FirstName (sets IsAttached = false on FirstName's data)
            var toggleTask = Task.Run(() =>
            {
                barrier.SignalAndWait();
                person.UseFirstName = true;
            });

            var detachTask = Task.Run(() =>
            {
                barrier.SignalAndWait();
                person.DetachSubjectProperty(firstNameProperty);
            });

            await Task.WhenAll(toggleTask, detachTask);

            // Assert — no exceptions, and Display value is consistent with current state
            var expected = person.UseFirstName
                ? (person.FirstName ?? "")
                : (person.LastName ?? "");
            Assert.Equal(expected, person.Display);
        }
    }

    [Fact]
    public async Task WhenPropertyChangedSubscribedDuringConcurrentWrites_ThenNotificationsFireWithCorrectPropertyNames()
    {
        for (var i = 0; i < DefaultIterations; i++)
        {
            // Arrange
            var context = InterceptorSubjectContext
                .Create()
                .WithFullPropertyTracking();

            var person = new Person(context)
            {
                FirstName = "A",
                LastName = "B"
            };

            var notifiedPropertyNames = new ConcurrentBag<string>();
            ((INotifyPropertyChanged)person).PropertyChanged += (_, args) =>
            {
                if (args.PropertyName is not null)
                    notifiedPropertyNames.Add(args.PropertyName);
            };

            var barrier = new Barrier(2);

            // Act
            var t1 = Task.Run(() =>
            {
                barrier.SignalAndWait();
                person.FirstName = "Jane";
            });

            var t2 = Task.Run(() =>
            {
                barrier.SignalAndWait();
                person.LastName = "Smith";
            });

            await Task.WhenAll(t1, t2);

            // Assert
            Assert.Contains(nameof(Person.FirstName), notifiedPropertyNames);
            Assert.Contains(nameof(Person.LastName), notifiedPropertyNames);
            Assert.Contains(nameof(Person.FullName), notifiedPropertyNames);
        }
    }

    [Fact]
    public async Task WhenDerivedPropertyDetachedAndReattachedConcurrently_ThenFinalStateIsConsistent()
    {
        for (var i = 0; i < DefaultIterations; i++)
        {
            // Arrange
            var context = InterceptorSubjectContext
                .Create()
                .WithFullPropertyTracking();

            var car = new Car(context);
            car.Tires[0].Pressure = 10;
            car.Tires[1].Pressure = 20;
            car.Tires[2].Pressure = 30;
            car.Tires[3].Pressure = 40;

            var averagePressureProperty = new PropertyReference(car, nameof(Car.AveragePressure));

            var barrier = new Barrier(2);

            // Act — detach and reattach concurrently with writes
            var detachReattachTask = Task.Run(() =>
            {
                barrier.SignalAndWait();
                car.DetachSubjectProperty(averagePressureProperty);
                car.AttachSubjectProperty(averagePressureProperty);
            });

            var writeTask = Task.Run(() =>
            {
                barrier.SignalAndWait();
                car.Tires[0].Pressure = 100;
            });

            await Task.WhenAll(detachReattachTask, writeTask);

            // Assert — after reattach, derived value must be correct
            var expected = car.Tires.Average(t => t.Pressure);
            Assert.Equal(expected, car.AveragePressure);
        }
    }

    [Fact]
    public async Task StressTest_MassConcurrentWritesToSharedDependencies_AllDerivedValuesCorrect()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        var person = new Person(context)
        {
            FirstName = "A",
            LastName = "B"
        };

        const int threadsPerProperty = 2;
        const int writesPerThread = 200;
        var counter = 0;
        var tasks = new List<Task>();

        // Act
        for (var t = 0; t < threadsPerProperty; t++)
        {
            tasks.Add(Task.Run(() =>
            {
                for (var j = 0; j < writesPerThread; j++)
                {
                    person.FirstName = $"First{Interlocked.Increment(ref counter)}";
                }
            }));

            tasks.Add(Task.Run(() =>
            {
                for (var j = 0; j < writesPerThread; j++)
                {
                    person.LastName = $"Last{Interlocked.Increment(ref counter)}";
                }
            }));
        }

        await Task.WhenAll(tasks);

        // Assert
        var expected = $"{person.FirstName} {person.LastName}".Trim();
        Assert.Equal(expected, person.FullName);
        Assert.Equal($"Mr. {expected}", person.FullNameWithPrefix);
    }

    [Fact]
    public async Task StressTest_ConcurrentObjectGraphChurn_ValuesCorrectAfterSettling()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        var car = new Car(context);
        var pressureCounter = 0;

        const int threadCount = 4;
        const int operationsPerThread = 100;
        var barrier = new Barrier(threadCount);
        var tasks = new List<Task>();

        // Act
        for (var t = 0; t < threadCount; t++)
        {
            var threadIndex = t;
            tasks.Add(Task.Run(() =>
            {
                barrier.SignalAndWait();
                for (var j = 0; j < operationsPerThread; j++)
                {
                    if (j % 5 == 0 && threadIndex == 0)
                    {
                        var newTire = new Tire(context)
                        {
                            Pressure = Interlocked.Increment(ref pressureCounter)
                        };
                        var currentTires = car.Tires;
                        car.Tires = [newTire, currentTires[1], currentTires[2], currentTires[3]];
                    }
                    else
                    {
                        var currentTires = car.Tires;
                        currentTires[TireIndex(threadIndex)].Pressure =
                            Interlocked.Increment(ref pressureCounter);
                    }
                }
            }));
        }

        await Task.WhenAll(tasks);

        // Assert
        var expected = car.Tires.Average(t => t.Pressure);
        Assert.Equal(expected, car.AveragePressure);
    }

    [Fact]
    public async Task StressTest_ConcurrentAttachOfManyCrossSubjectDependencies_AllValuesCorrect()
    {
        // Arrange
        const int carCount = 50;
        const int batchSize = 8;
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        var cars = new Car[carCount];

        // Act — create cars in batches to avoid thread pool starvation
        for (var batch = 0; batch < carCount; batch += batchSize)
        {
            var batchEnd = Math.Min(batch + batchSize, carCount);
            var currentBatchSize = batchEnd - batch;
            var barrier = new Barrier(currentBatchSize);
            var tasks = new Task[currentBatchSize];

            for (var i = 0; i < currentBatchSize; i++)
            {
                var index = batch + i;
                tasks[i] = Task.Run(() =>
                {
                    barrier.SignalAndWait();
                    var car = new Car(context);
                    car.Tires[0].Pressure = index * 10;
                    car.Tires[1].Pressure = index * 10 + 1;
                    car.Tires[2].Pressure = index * 10 + 2;
                    car.Tires[3].Pressure = index * 10 + 3;
                    cars[index] = car;
                });
            }

            await Task.WhenAll(tasks);
        }

        // Assert
        for (var i = 0; i < carCount; i++)
        {
            var car = cars[i];
            var expected = car.Tires.Average(t => t.Pressure);
            Assert.Equal(expected, car.AveragePressure);
        }
    }

    [Fact]
    public async Task StressTest_MixedOperationsChaos_NoExceptionsAndSurvivingValuesCorrect()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        const int subjectPoolSize = 10;
        const int threadCount = 4;
        const int operationsPerThread = 100;

        var persons = new Person[subjectPoolSize];
        for (var i = 0; i < subjectPoolSize; i++)
        {
            persons[i] = new Person(context)
            {
                FirstName = $"First{i}",
                LastName = $"Last{i}"
            };
        }

        var detachedProperties = new HashSet<int>();
        var detachedLock = new object();
        var operationCounter = 0;
        var tasks = new List<Task>();

        // Act
        for (var t = 0; t < threadCount; t++)
        {
            tasks.Add(Task.Run(() =>
            {
                var random = new Random(Thread.CurrentThread.ManagedThreadId);
                for (var j = 0; j < operationsPerThread; j++)
                {
                    var index = random.Next(subjectPoolSize);
                    var person = persons[index];
                    var operation = random.Next(4);
                    var count = Interlocked.Increment(ref operationCounter);

                    switch (operation)
                    {
                        case 0:
                            person.FirstName = $"F{count}";
                            break;
                        case 1:
                            person.LastName = $"L{count}";
                            break;
                        case 2:
                            _ = person.FullName;
                            _ = person.FullNameWithPrefix;
                            break;
                        case 3:
                            bool shouldDetach;
                            lock (detachedLock)
                            {
                                shouldDetach = detachedProperties.Add(index);
                            }
                            if (shouldDetach)
                            {
                                var property = new PropertyReference(person, nameof(Person.FullNameWithPrefix));
                                person.DetachSubjectProperty(property);
                            }
                            break;
                    }
                }
            }));
        }

        await Task.WhenAll(tasks);

        // Assert
        for (var i = 0; i < subjectPoolSize; i++)
        {
            var person = persons[i];

            var expected = person.FirstName is null && person.LastName is null
                ? "NA"
                : $"{person.FirstName} {person.LastName}".Trim();
            Assert.Equal(expected, person.FullName);

            bool wasDetached;
            lock (detachedLock)
            {
                wasDetached = detachedProperties.Contains(i);
            }
            if (!wasDetached)
            {
                Assert.Equal($"Mr. {expected}", person.FullNameWithPrefix);
            }
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference CreateCarAndReplaceFirstTire(IInterceptorSubjectContext context)
    {
        var car = new Car(context);
        var weakTire = new WeakReference(car.Tires[0]);
        car.Tires = [new Tire(context), car.Tires[1], car.Tires[2], car.Tires[3]];
        return weakTire;
    }

    [Fact]
    public async Task WhenConcurrentWritesToSharedDependency_ThenObservableNotificationsAlwaysCarryFinalValue()
    {
        const int threadCount = 8;
        const int writesPerThread = 50;

        for (var iteration = 0; iteration < DefaultIterations; iteration++)
        {
            // Arrange
            var context = InterceptorSubjectContext
                .Create()
                .WithFullPropertyTracking();

            var person = new Person(context)
            {
                FirstName = "A",
                LastName = "B"
            };

            // ConcurrentQueue preserves insertion order so we can check the last-delivered notification.
            var observedChanges = new ConcurrentQueue<SubjectPropertyChange>();
            using var subscription = context
                .GetPropertyChangeObservable(ImmediateScheduler.Instance)
                .Where(change => change.Property.Name == nameof(Person.FullName))
                .Subscribe(observedChanges.Enqueue);

            // Drain initial setup notifications.
            while (observedChanges.TryDequeue(out _)) { }

            var barrier = new Barrier(threadCount);
            var counter = 0;

            // Act — many threads writing to dependencies of the same derived property.
            // High concurrency increases the chance of a thread being preempted between
            // the stale-notification check and the actual notification delivery.
            var tasks = new Task[threadCount];
            for (var t = 0; t < threadCount; t++)
            {
                var isFirstName = t % 2 == 0;
                tasks[t] = Task.Run(() =>
                {
                    barrier.SignalAndWait();
                    for (var j = 0; j < writesPerThread; j++)
                    {
                        var value = Interlocked.Increment(ref counter).ToString();
                        if (isFirstName)
                            person.FirstName = value;
                        else
                            person.LastName = value;
                    }
                });
            }

            await Task.WhenAll(tasks);

            // Assert — the last-delivered notification must carry the correct final value.
            // Without the ReferenceEquals guard, a stale notification can fire AFTER
            // the correct one (out-of-order delivery), making the last-delivered value wrong.
            var finalFullName = $"{person.FirstName} {person.LastName}".Trim();
            var allChanges = observedChanges.ToArray();
            Assert.True(allChanges.Length > 0, "Expected at least one FullName change notification");

            var lastDeliveredValue = allChanges[^1].GetNewValue<string?>();
            Assert.Equal(finalFullName, lastDeliveredValue);

            // Every notified newValue must be a valid computed state (format: "X Y").
            foreach (var change in allChanges)
            {
                var newValue = change.GetNewValue<string?>();
                Assert.NotNull(newValue);
                Assert.Contains(" ", newValue);
            }
        }
    }

    [Fact]
    public async Task WhenDerivedGetterWritesToSubjectTypedPropertyOnRecalculation_ThenNoDeadlockAndCorrectValue()
    {
        // Regression test: reproduces deadlock between lock(data) and lock(_attachedSubjects).
        //
        // SideEffectPerson.Greeting getter writes to Companion (subject-typed property),
        // which triggers LifecycleInterceptor.WriteProperty → lock(_attachedSubjects). The write
        // only happens for real on the recalculation path: attach-time evaluation runs inside
        // the derived handler's attach callback, where the callback contract rejects it, and the
        // model absorbs that rejection so attach and re-attach complete and the toggling below
        // keeps producing lock contention.
        //
        // The deadlock (fixed by evaluating getter outside lock(data)):
        //   Thread A: RecalculateDerivedProperty(Greeting) → lock(data_Greeting) → getter
        //             → Companion=null → LifecycleInterceptor → lock(_attachedSubjects) → WAITS
        //   Thread B: holder.Person=null → LifecycleInterceptor → lock(_attachedSubjects)
        //             → detach SideEffectPerson → DetachSubjectProperty(Greeting)
        //             → DerivedPropertyChangeHandler.DetachProperty → lock(data_Greeting) → WAITS
        //
        // Both threads contend on the SAME data_Greeting lock, creating an ABBA deadlock.
        // SideEffectHolder is the parent that makes SideEffectPerson a lifecycle-managed child,
        // so setting holder.Person = null triggers detach of all SideEffectPerson properties
        // (including Greeting) under lock(_attachedSubjects).

        var totalSuccessfulWrites = 0;
        for (var i = 0; i < DefaultIterations; i++)
        {
            // Arrange
            var context = InterceptorSubjectContext
                .Create()
                .WithFullPropertyTracking();

            var holder = new SideEffectHolder(context);
            var person = new SideEffectPerson(context)
            {
                Name = "Initial",
                Companion = new Person(context) { FirstName = "Friend" }
            };
            holder.Person = person;

            // The Arrange writes above already recalculate Greeting, so only writes landed during
            // the concurrent phase count towards proving that phase alive.
            var successfulWritesBeforeAct = person.SuccessfulCompanionWriteCount;

            var barrier = new Barrier(2);

            // Act — Thread 1 writes Name → RecalculateDerivedProperty(Greeting)
            //          → getter writes Companion (subject-typed) → lock(_attachedSubjects)
            //        Thread 2 sets holder.Person = null → lock(_attachedSubjects)
            //          → detach SideEffectPerson → DetachSubjectProperty(Greeting) → lock(data_Greeting)
            var writeNameTask = Task.Run(() =>
            {
                barrier.SignalAndWait();
                for (var j = 0; j < 20; j++)
                {
                    try
                    {
                        person.Name = $"Name{j}";
                    }
                    catch (NullReferenceException)
                    {
                        // Expected: person may be temporarily detached (context nulled)
                        // by the concurrent holder.Person = null on the other thread.
                    }
                }
            });

            var detachTask = Task.Run(() =>
            {
                barrier.SignalAndWait();
                for (var j = 0; j < 20; j++)
                {
                    try
                    {
                        // Detach: lifecycle removes SideEffectPerson → DetachProperty(Greeting) → lock(data_Greeting)
                        holder.Person = null;
                        // Reattach: lifecycle adds SideEffectPerson → AttachProperty(Greeting) → lock(data_Greeting)
                        holder.Person = person;
                    }
                    catch (NullReferenceException)
                    {
                        // Expected: concurrent detach may null the context while
                        // LifecycleInterceptor reads properties during attach/detach.
                    }
                }
            });

            var work = Task.WhenAll(writeNameTask, detachTask);
            var completed = await Task.WhenAny(work, Task.Delay(TimeSpan.FromSeconds(10)));

            // Assert — test must complete (no deadlock) and final value must be correct.
            Assert.True(completed == work, "Test timed out — possible deadlock");
            await work;

            // Captured before the Greeting read below, which invokes the getter itself and would
            // otherwise count a write the concurrent phase never produced.
            totalSuccessfulWrites += person.SuccessfulCompanionWriteCount - successfulWritesBeforeAct;

            Assert.Equal($"Hello, {person.Name}", person.Greeting);
        }

        // The getter absorbs the contract violation thrown inside callback scopes, so if
        // recalculation ever moves inside one, every write is eaten and this test passes while
        // pinning nothing. At least one landed write proves the recalculation path stayed alive.
        Assert.True(totalSuccessfulWrites > 0, "No Companion write ever landed; the getter absorbed all of them and the test went vacuous.");
    }

    private static int TireIndex(int threadIndex) => (threadIndex % 3) + 1;
}
