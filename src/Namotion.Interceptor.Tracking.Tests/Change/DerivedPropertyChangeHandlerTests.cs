using System.Reactive.Concurrency;
using System.Reactive.Linq;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Tests.Models;
using Namotion.Interceptor.Tracking.Transactions;

namespace Namotion.Interceptor.Tracking.Tests.Change;

public class DerivedPropertyChangeHandlerTests
{
    [Fact]
    public void WhenChangingPropertyWhichIsUsedInDerivedProperty_ThenDerivedPropertyIsChanged()
    {
        // Arrange
        var changes = new List<SubjectPropertyChange>();
        var context = InterceptorSubjectContext
            .Create()
            .WithDerivedPropertyChangeDetection()
            .WithPropertyChangeSubscriptions();

        context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(changes.Add);

        // Act
        _ = new Person(context)
        {
            FirstName = "Rico",
            LastName = "Suter"
        };

        // Assert
        Assert.Contains(changes, c =>
            c.Property.Name == nameof(Person.FullName) &&
            c.GetOldValue<string?>() == "NA" &&
            c.GetNewValue<string?>() == "Rico");

        Assert.Contains(changes, c =>
            c.Property.Name == nameof(Person.FullName) &&
            c.GetOldValue<string?>() == "Rico" &&
            c.GetNewValue<string?>() == "Rico Suter");

        Assert.Contains(changes, c =>
            c.Property.Name == nameof(Person.FullNameWithPrefix) &&
            c.GetNewValue<string?>() == "Mr. Rico Suter");
    }

    [Fact]
    public void WhenTrackingDerivedPropertiesUsingPropertiesFromOtherSubjectsAndInheritance_ThenChangesAreTracked()
    {
        // Arrange
        var changes = new List<SubjectPropertyChange>();
        var context = InterceptorSubjectContext
            .Create()
            .WithDerivedPropertyChangeDetection()
            .WithPropertyChangeSubscriptions()
            .WithContextInheritance();
        
        // Act
        var car = new Car(context);
        
        context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(changes.Add);
        
        car.Tires[0].Pressure = 2.0m;       
        
        // Assert
        Assert.Contains(changes, c => c.Property.Name == "AveragePressure");
    }

    [Fact]
    public void WhenDerivedPropertyChanges_ThenTimestampIsConsistentWithMutationContext()
    {
        // Arrange
        var changes = new List<SubjectPropertyChange>();
        var context = InterceptorSubjectContext
            .Create()
            .WithDerivedPropertyChangeDetection()
            .WithPropertyChangeSubscriptions();

        var dateTime = DateTimeOffset.UtcNow.AddHours(-1);
        var person = new Person(context);
        context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Where(c => c.Property.Name == nameof(Person.FullName))
            .Subscribe(changes.Add);

        // Act
        for (var i = 0; i < 10000; i++)
        {
            var dt = dateTime.AddSeconds(i);
            using (SubjectChangeContext.WithChangedTimestamp(dt))
            {
                person.FirstName = dt.ToString();
            }
        }

        // Assert
        Assert.Equal(10000, changes.Count);
        foreach (var c in changes)
        {
            // the fullname should contain the timestamp as firstname (trimmed, no trailing space)
            Assert.Equal($"{c.ChangedTimestamp}", c.GetNewValue<string>());
        }
    }

    [Fact]
    public void WhenNestedDerivedPropertiesChange_ThenAllLevelsAreRecalculated()
    {
        // Arrange
        var changes = new List<SubjectPropertyChange>();
        var context = InterceptorSubjectContext
            .Create()
            .WithDerivedPropertyChangeDetection()
            .WithPropertyChangeSubscriptions();

        context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(changes.Add);

        var person = new Person(context)
        {
            FirstName = "John",
            LastName = "Doe"
        };

        changes.Clear();

        // Act - Change base property
        person.FirstName = "Jane";

        // Assert - All derived levels should update
        // Level 1: FullName depends on FirstName/LastName
        Assert.Contains(changes, c =>
            c.Property.Name == nameof(Person.FullName) &&
            c.GetOldValue<string?>() == "John Doe" &&
            c.GetNewValue<string?>() == "Jane Doe");

        // Level 2: FullNameWithPrefix depends on FullName (nested derived)
        Assert.Contains(changes, c =>
            c.Property.Name == nameof(Person.FullNameWithPrefix) &&
            c.GetOldValue<string?>() == "Mr. John Doe" &&
            c.GetNewValue<string?>() == "Mr. Jane Doe");
    }

    [Fact]
    public void WhenDependenciesChange_ThenOldDependenciesAreCleanedUp()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithDerivedPropertyChangeDetection();

        var person = new Person(context)
        {
            FirstName = "John",
            LastName = "Doe"
        };

        // Get the FullName derived property reference
        var fullNameProp = new PropertyReference(person, nameof(Person.FullName));

        // Initially FullName depends on FirstName and LastName
        var initialDeps = fullNameProp.GetRequiredProperties();
        Assert.Equal(2, initialDeps.Length);

        // Act - Change FirstName which will re-record dependencies
        person.FirstName = "Jane";

        // Assert - Should still have 2 dependencies (FirstName, LastName)
        var afterDeps = fullNameProp.GetRequiredProperties();
        Assert.Equal(2, afterDeps.Length);
    }

    [Fact]
    public void WhenMultipleDerivedPropertiesDependOnSameSource_ThenAllAreUpdated()
    {
        // Arrange
        var changes = new List<SubjectPropertyChange>();
        var context = InterceptorSubjectContext
            .Create()
            .WithDerivedPropertyChangeDetection()
            .WithPropertyChangeSubscriptions();

        context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(changes.Add);

        var person = new Person(context)
        {
            FirstName = "John",
            LastName = "Doe"
        };

        changes.Clear();

        // Act - Change FirstName (both FullName and FullNameWithPrefix depend on it)
        person.FirstName = "Jane";

        // Assert - Both derived properties should update
        Assert.Contains(changes, c => c.Property.Name == nameof(Person.FullName));
        Assert.Contains(changes, c => c.Property.Name == nameof(Person.FullNameWithPrefix));

        // FullName should update once
        Assert.Single(changes, c => c.Property.Name == nameof(Person.FullName));

        // FullNameWithPrefix should update once
        Assert.Single(changes, c => c.Property.Name == nameof(Person.FullNameWithPrefix));
    }

    [Fact]
    public void WhenSourcePropertyChangedMultipleTimes_ThenDerivedPropertyUpdatesCorrectly()
    {
        // Arrange
        var changes = new List<SubjectPropertyChange>();
        var context = InterceptorSubjectContext
            .Create()
            .WithDerivedPropertyChangeDetection()
            .WithPropertyChangeSubscriptions();

        context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Where(c => c.Property.Name == nameof(Person.FullName))
            .Subscribe(changes.Add);

        var person = new Person(context)
        {
            FirstName = "John",
            LastName = "Doe"
        };

        changes.Clear();

        // Act - Change FirstName multiple times rapidly
        person.FirstName = "Jane";
        person.FirstName = "Jack";
        person.FirstName = "Jim";

        // Assert
        Assert.Equal(3, changes.Count);
        Assert.Equal("Jane Doe", changes[0].GetNewValue<string?>());
        Assert.Equal("Jack Doe", changes[1].GetNewValue<string?>());
        Assert.Equal("Jim Doe", changes[2].GetNewValue<string?>());
    }

    [Fact]
    public void WhenDerivedPropertyAccessesMultipleObjects_ThenAllDependenciesAreTracked()
    {
        // Arrange
        var changes = new List<SubjectPropertyChange>();
        var context = InterceptorSubjectContext
            .Create()
            .WithDerivedPropertyChangeDetection()
            .WithPropertyChangeSubscriptions()
            .WithContextInheritance();

        var car = new Car(context);

        context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Where(c => c.Property.Name == nameof(Car.AveragePressure))
            .Subscribe(changes.Add);

        // Act - Change pressure on different tires
        car.Tires[0].Pressure = 2.0m;
        car.Tires[1].Pressure = 2.2m;
        car.Tires[2].Pressure = 2.1m;
        car.Tires[3].Pressure = 2.3m;

        // Assert - AveragePressure should update for each tire change
        Assert.Equal(4, changes.Count);

        // Verify average is calculated correctly each time
        Assert.Equal((2.0m + 0 + 0 + 0) / 4, changes[0].GetNewValue<decimal>());
        Assert.Equal((2.0m + 2.2m + 0 + 0) / 4, changes[1].GetNewValue<decimal>());
        Assert.Equal((2.0m + 2.2m + 2.1m + 0) / 4, changes[2].GetNewValue<decimal>());
        Assert.Equal((2.0m + 2.2m + 2.1m + 2.3m) / 4, changes[3].GetNewValue<decimal>());
    }

    [Fact]
    public void WhenChangingPropertyWhichIsNotUsedInDerivedProperty_ThenNoRecalculationOccurs()
    {
        // Arrange
        var recalculationCount = 0;
        var context = InterceptorSubjectContext
            .Create()
            .WithDerivedPropertyChangeDetection()
            .WithPropertyChangeSubscriptions();
        
        var person = new Person(context)
        {
            FirstName = "John",
            LastName = "Doe"
        };

        context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Where(c => c.Property.Name == nameof(Person.FullName))
            .Subscribe(_ => recalculationCount++);

        // Act - Change an unrelated property
        person.Father = new Person();

        // Assert - No recalculation should occur
        Assert.Equal(0, recalculationCount);
    }

    [Fact]
    public void WhenBacklinkIsMaintained_ThenSourcePropertyKnowsItsUsedBy()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithDerivedPropertyChangeDetection();

        var person = new Person(context)
        {
            FirstName = "John",
            LastName = "Doe"
        };

        // Act - Access derived property to establish dependencies
        _ = person.FullName;

        // Assert - FirstName should know it's used by FullName
        var firstNameProp = new PropertyReference(person, nameof(Person.FirstName));
        var usedBy = firstNameProp.GetUsedByProperties();

        Assert.True(usedBy.Count > 0, "FirstName should have dependent properties");

        // Check that FullName is in the usedBy list
        var usedByArray = usedBy.Items.ToArray();
        Assert.Contains(usedByArray, p => p.Name == nameof(Person.FullName));
    }
    
    [Fact]
    public async Task WhenPropertyIsChanged_ThenTimestampIsCorrectInDerivedPropertyChanges()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        var person = new Person(context)
        {
            FirstName = "Parent"
        };

        var changes = new List<SubjectPropertyChange>();
        using var _ = context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(c => changes.Add(c));

        // Act
        var date = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using (SubjectChangeContext.WithChangedTimestamp(date))
        {
            person.Mother = new Person // should trigger only mother property update itself (no new object properties)
            {
                FirstName = "Mother"
            };
            person.Mother.LastName = "MyMotherLN"; // should trigger FullName and FullNameWithPrefix updates
        }

        var update1 = changes
            .Select(c => $"{c.ChangedTimestamp:O}: {((Person)c.Property.Subject).FirstName}.{c.Property.Name} = {c.GetNewValue<object?>()}")
            .ToArray();

        changes.Clear();

        using (SubjectChangeContext.WithChangedTimestamp(date.AddSeconds(1)))
        {
            person.FirstName = "Parent-Updated"; // should trigger FullName and FullNameWithPrefix updates
            person.Mother = null;
        }

        var update2 = changes
            .Select(c => $"{c.ChangedTimestamp:O}: {((Person)c.Property.Subject).FirstName}.{c.Property.Name} = {c.GetNewValue<object?>()}")
            .ToArray();

        changes.Clear();

        // Assert
        await Verify(new
        {
            Update1 = update1,
            Update2 = update2,
        });
    }

    [Fact]
    public void WhenSourcePropertyChanges_ThenDerivedPropertyFiresPropertyChanged()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithDerivedPropertyChangeDetection();

        var person = new Person(context)
        {
            FirstName = "John",
            LastName = "Doe"
        };

        var firedEvents = new List<string>();
        person.PropertyChanged += (s, e) => firedEvents.Add(e.PropertyName!);

        // Act
        person.FirstName = "Jane";

        // Assert
        Assert.Contains("FirstName", firedEvents);
        Assert.Contains("FullName", firedEvents);
    }

    [Fact]
    public void WhenSourceChanges_ThenNestedDerivedPropertiesFirePropertyChanged()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithDerivedPropertyChangeDetection();

        var person = new Person(context)
        {
            FirstName = "John",
            LastName = "Doe"
        };

        var firedEvents = new List<string>();
        person.PropertyChanged += (s, e) => firedEvents.Add(e.PropertyName!);

        // Act
        person.FirstName = "Jane";

        // Assert - All levels should fire: FirstName -> FullName -> FullNameWithPrefix
        Assert.Contains("FirstName", firedEvents);
        Assert.Contains("FullName", firedEvents);
        Assert.Contains("FullNameWithPrefix", firedEvents);
    }

    [Fact]
    public void WhenSourceChanges_ThenAllDependentDerivedPropertiesFirePropertyChangedOnce()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithDerivedPropertyChangeDetection();

        var person = new Person(context)
        {
            FirstName = "John",
            LastName = "Doe"
        };

        var firedEvents = new List<string>();
        person.PropertyChanged += (s, e) => firedEvents.Add(e.PropertyName!);

        // Act
        person.FirstName = "Jane";

        // Assert - Each derived property should fire exactly once (no duplicates)
        Assert.Single(firedEvents, e => e == "FullName");
        Assert.Single(firedEvents, e => e == "FullNameWithPrefix");
    }

    [Fact]
    public void WhenAWriteIsVetoedAfterTheDerivedHandler_ThenNoWriteTimestampIsBumped()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        context.AddService<IWriteInterceptor>(new VetoingWriteInterceptor());

        var initialTimestamp = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        DerivedSetterPerson person;
        using (SubjectChangeContext.WithChangedTimestamp(initialTimestamp))
        {
            person = new DerivedSetterPerson(context);
            _ = person.NicknameWithPrefix;
        }

        var nickname = new PropertyReference(person, nameof(DerivedSetterPerson.Nickname));
        var dependent = new PropertyReference(person, nameof(DerivedSetterPerson.NicknameWithPrefix));
        var nicknameBefore = nickname.TryGetWriteTimestamp();
        var dependentBefore = dependent.TryGetWriteTimestamp();
        var vetoTimestamp = initialTimestamp.AddSeconds(1);

        // Act
        using (SubjectChangeContext.WithChangedTimestamp(vetoTimestamp))
        {
            person.Nickname = "vetoed";
        }

        // Assert
        Assert.Null(person.Nickname);
        Assert.Equal(nicknameBefore, nickname.TryGetWriteTimestamp());
        Assert.Equal(dependentBefore, dependent.TryGetWriteTimestamp());
    }

    [Fact]
    public async Task WhenTransactionIsOpenOnAnotherContext_ThenDerivedPropertiesStillRecalculate()
    {
        // Arrange: the written context has no transaction interceptor, so the transaction open on the
        // other context never captures the write and no commit will replay its cascade. The ambient
        // transaction slot is process-wide, so the handler still sees that transaction.
        var transactionContext = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithTransactions();

        var context = InterceptorSubjectContext
            .Create()
            .WithDerivedPropertyChangeDetection()
            .WithPropertyChangeSubscriptions();

        var transactionPerson = new Person(transactionContext);
        var person = new Person(context)
        {
            FirstName = "John",
            LastName = "Doe"
        };

        var changedProperties = new List<string>();
        using var subscription = context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(change => changedProperties.Add(change.Property.Name));

        // Act
        using (await transactionContext.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            transactionPerson.FirstName = "Captured";
            person.FirstName = "Jane";
        }

        // Assert
        Assert.Contains(nameof(Person.FirstName), changedProperties);
        Assert.Contains(nameof(Person.FullName), changedProperties);
        Assert.Contains(nameof(Person.FullNameWithPrefix), changedProperties);
    }

    [Fact]
    public void WhenATransactionBecomesAmbientDuringAnUncapturedWrite_ThenTheDependentsStillRecalculate()
    {
        // Arrange: no transaction is ambient when the write starts, so the transaction interceptor takes its
        // fast path and the write reaches the model. A synchronous observer then opens one, which publishes
        // it into the ambient slot of the flow the write is running on, so by the time the cascade is
        // decided a live transaction is bound to this context for a write nothing captured.
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithTransactions();

        var person = new Person(context) { LastName = "Doe" };
        _ = person.FullName;

        SubjectTransaction? ambientTransaction = null;
        var changedProperties = new List<string>();
        using var subscription = context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(change =>
            {
                changedProperties.Add(change.Property.Name);
                if (change.Property.Name == nameof(Person.FirstName) && ambientTransaction is null)
                {
                    ambientTransaction = context
                        .BeginTransactionAsync(TransactionFailureHandling.BestEffort)
                        .GetAwaiter()
                        .GetResult();
                }
            });

        // Act
        try
        {
            person.FirstName = "Jane";
        }
        finally
        {
            // Disposal releases a process-wide active count, so leaking it here would leave every later
            // test in the assembly believing a transaction is open.
            ambientTransaction?.Dispose();
        }

        // Assert: the model holds the recalculated value either way, so only the announcements discriminate.
        Assert.Contains(nameof(Person.FullName), changedProperties);
        Assert.Contains(nameof(Person.FullNameWithPrefix), changedProperties);
    }

    [Fact]
    public async Task WhenATransactionIsRolledBack_ThenNoDependentValueWasAnnouncedFromItsPendingState()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithTransactions();

        var subject = new TransactionCascadeSubject(context)
        {
            Plain = "committed",
            DerivedWithSetter = "d0"
        };
        _ = subject.Combined;

        var announced = new List<SubjectPropertyChange>();
        using var subscription = context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Where(change => change.Property.Name == nameof(TransactionCascadeSubject.Combined))
            .Subscribe(announced.Add);

        // Act
        using (await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            subject.Plain = "uncommitted";
            subject.DerivedWithSetter = "d1";

            var landedChange = Assert.Single(announced);
            Assert.Equal("committed|d1", landedChange.GetNewValue<string>());
            Assert.DoesNotContain(announced, change => change.GetNewValue<string>() == "uncommitted|d1");
            Assert.Equal("uncommitted|d1", subject.Combined);
        }

        // Assert
        Assert.Equal("committed|d1", subject.Combined);

        subject.DerivedWithSetter = "d2";

        var subsequentChange = Assert.Single(
            announced,
            change => change.GetNewValue<string>() == "committed|d2");
        Assert.Equal("committed|d1", subsequentChange.GetOldValue<string>());
    }

    [Fact]
    public async Task WhenADerivedSetterLandsWithAPendingDependency_ThenDerivedOfDerivedUsesTheModelView()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithTransactions();
        var subject = new TransactionCascadeSubject(context)
        {
            Plain = "committed",
            DerivedWithSetter = "d0"
        };
        _ = subject.CombinedAgain;

        var changes = new List<SubjectPropertyChange>();
        using var subscription = context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Where(change => change.Property.Name is
                nameof(TransactionCascadeSubject.Combined) or
                nameof(TransactionCascadeSubject.CombinedAgain))
            .Subscribe(changes.Add);

        // Act
        using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            subject.Plain = "pending";
            subject.DerivedWithSetter = "d1";

            // Assert
            Assert.Collection(
                changes,
                change =>
                {
                    Assert.Equal(nameof(TransactionCascadeSubject.Combined), change.Property.Name);
                    Assert.Equal("committed|d0", change.GetOldValue<string>());
                    Assert.Equal("committed|d1", change.GetNewValue<string>());
                },
                change =>
                {
                    Assert.Equal(nameof(TransactionCascadeSubject.CombinedAgain), change.Property.Name);
                    Assert.Equal("[committed|d0]", change.GetOldValue<string>());
                    Assert.Equal("[committed|d1]", change.GetNewValue<string>());
                });

            await transaction.CommitAsync(CancellationToken.None);
        }

        Assert.Contains(changes, change =>
            change.Property.Name == nameof(TransactionCascadeSubject.Combined) &&
            change.GetNewValue<string>() == "pending|d1");
        Assert.Contains(changes, change =>
            change.Property.Name == nameof(TransactionCascadeSubject.CombinedAgain) &&
            change.GetNewValue<string>() == "[pending|d1]");
        Assert.Equal("[pending|d1]", subject.CombinedAgain);
    }

    [Fact]
    public async Task WhenOneDependentReadsAPendingProperty_ThenEveryDependentStillRecalculates()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithTransactions();
        var subject = new TransactionCascadeSubject(context)
        {
            Plain = "committed",
            DerivedWithSetter = "d0"
        };
        _ = subject.Combined;
        _ = subject.Independent;

        var independentChanges = new List<SubjectPropertyChange>();
        using var subscription = context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Where(change => change.Property.Name == nameof(TransactionCascadeSubject.Independent))
            .Subscribe(independentChanges.Add);

        // Act
        using (await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            subject.Plain = "pending";
            subject.DerivedWithSetter = "d1";

            // Assert
            var change = Assert.Single(independentChanges);
            Assert.Equal("d0", change.GetOldValue<string?>());
            Assert.Equal("d1", change.GetNewValue<string?>());
        }
    }

    [Fact]
    public async Task WhenACommitLandsThePendingDependency_ThenTheFinalCommittedDerivedValuesAreAnnounced()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithTransactions();
        var subject = new TransactionCascadeSubject(context)
        {
            Plain = "committed",
            DerivedWithSetter = "d0"
        };
        _ = subject.CombinedAgain;

        var changes = new List<SubjectPropertyChange>();
        using var subscription = context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Where(change => change.Property.Name is
                nameof(TransactionCascadeSubject.Combined) or
                nameof(TransactionCascadeSubject.CombinedAgain))
            .Subscribe(changes.Add);

        // Act
        using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            subject.Plain = "committed-after-commit";
            await transaction.CommitAsync(CancellationToken.None);
        }

        // Assert
        Assert.Contains(changes, change =>
            change.Property.Name == nameof(TransactionCascadeSubject.Combined) &&
            change.GetOldValue<string>() == "committed|d0" &&
            change.GetNewValue<string>() == "committed-after-commit|d0");
        Assert.Contains(changes, change =>
            change.Property.Name == nameof(TransactionCascadeSubject.CombinedAgain) &&
            change.GetOldValue<string>() == "[committed|d0]" &&
            change.GetNewValue<string>() == "[committed-after-commit|d0]");
    }

    [Fact]
    public async Task WhenADerivedPropertyIsAttachedInsideATransaction_ThenItsCachedValueUsesTheModelView()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithTransactions();
        var subject = new TransactionCascadeSubject(context)
        {
            Plain = "committed",
            DerivedWithSetter = "d0"
        };
        var property = new PropertyReference(subject, nameof(TransactionCascadeSubject.Combined));
        var lifecycleChange = new SubjectPropertyLifecycleChange((IInterceptorSubject)subject, property);
        var handler = context.TryGetService<DerivedPropertyChangeHandler>()!;
        handler.DetachProperty(lifecycleChange);

        // Act
        using (await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            subject.Plain = "pending";
            handler.AttachProperty(lifecycleChange);
        }

        var changes = new List<SubjectPropertyChange>();
        using var subscription = context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Where(change => change.Property.Name == nameof(TransactionCascadeSubject.Combined))
            .Subscribe(changes.Add);
        subject.DerivedWithSetter = "d1";

        // Assert
        var change = Assert.Single(changes);
        Assert.Equal("committed|d0", change.GetOldValue<string>());
        Assert.Equal("committed|d1", change.GetNewValue<string>());
    }

    [Fact]
    public async Task WhenDerivedEvaluationRunsInsideATransaction_ThenTheAmbientTransactionIdentityIsPreserved()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithTransactions();
        var subject = new TransactionCascadeSubject(context) { Plain = "committed" };
        SubjectTransaction? observedTransaction = null;
        subject.ProbeEvaluator = value =>
        {
            observedTransaction = SubjectTransaction.Current;
            return $"{value.Plain}|evaluated";
        };

        var changes = new List<SubjectPropertyChange>();
        using var subscription = context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Where(change => change.Property.Name == nameof(TransactionCascadeSubject.Probe))
            .Subscribe(changes.Add);

        // Act
        using (var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            subject.Plain = "pending";
            new PropertyReference(subject, nameof(TransactionCascadeSubject.Probe)).RecalculateDerivedProperty();

            // Assert
            Assert.Same(transaction, observedTransaction);
            Assert.Same(transaction, SubjectTransaction.Current);
        }

        Assert.Equal("committed|evaluated", Assert.Single(changes).GetNewValue<string>());
    }

    [Fact]
    public async Task WhenAGetterWritesANonDerivedProperty_ThenThatSideEffectIsStillCaptured()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithTransactions();
        var subject = new TransactionCascadeSubject(context) { Plain = "committed" };
        subject.ProbeEvaluator = value =>
        {
            value.SideEffect = "captured-side-effect";
            return $"{value.Plain}|evaluated";
        };

        // Act
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort);
        subject.Plain = "pending";
        new PropertyReference(subject, nameof(TransactionCascadeSubject.Probe)).RecalculateDerivedProperty();

        // Assert
        Assert.Contains(transaction.GetPendingChanges(), change =>
            change.Property.Name == nameof(TransactionCascadeSubject.SideEffect) &&
            change.GetNewValue<string?>() == "captured-side-effect");

        string? modelSideEffect = null;
        var flowControl = ExecutionContext.SuppressFlow();
        Task readTask;
        try
        {
            readTask = Task.Run(() => modelSideEffect = subject.SideEffect);
        }
        finally
        {
            flowControl.Undo();
        }

        await readTask;
        Assert.Null(modelSideEffect);
    }

    [Fact]
    public async Task WhenAGetterPublishesALandedSideEffect_ThenItsSynchronousSubscriberUsesTheModelView()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithTransactions();
        var subject = new TransactionCascadeSubject(context)
        {
            Plain = "committed",
            DerivedWithSetter = "d0"
        };
        _ = subject.Combined;

        string? valueReadBySubscriber = null;
        using var subscription = context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Where(change => change.Property.Name == nameof(TransactionCascadeSubject.Combined))
            .Subscribe(_ => valueReadBySubscriber = subject.Plain);
        subject.ProbeEvaluator = value =>
        {
            value.DerivedWithSetter = "d1";
            return "outer";
        };

        // Act
        using (await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            subject.Plain = "pending";
            new PropertyReference(subject, nameof(TransactionCascadeSubject.Probe)).RecalculateDerivedProperty();

            // Assert
            Assert.Equal("committed", valueReadBySubscriber);
            Assert.Equal("pending", subject.Plain);
        }
    }

    [Fact]
    public async Task WhenAGetterTriggersAReentrantDerivedRecalculation_ThenTheOuterRecorderFrameAndModelViewRemainIntact()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithTransactions();
        var subject = new TransactionCascadeSubject(context)
        {
            Plain = "plain-model",
            SideEffect = "side-model",
            DerivedWithSetter = "d0",
            ExternalSuffix = "before"
        };
        _ = subject.Combined;
        var property = new PropertyReference(subject, nameof(TransactionCascadeSubject.Probe));
        var writeInnerValue = 0;
        subject.ProbeEvaluator = value =>
        {
            var plain = value.Plain;
            if (Interlocked.Exchange(ref writeInnerValue, 1) == 0)
            {
                value.DerivedWithSetter = "d1";
            }
            return $"{plain}|{value.SideEffect}|{value.ExternalSuffix}";
        };

        var innerSubscriberReads = new List<string?>();
        var probeChanges = new List<SubjectPropertyChange>();
        using var subscription = context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(change =>
            {
                if (change.Property.Name == nameof(TransactionCascadeSubject.Combined))
                {
                    innerSubscriberReads.Add($"{subject.Plain}|{subject.SideEffect}");
                }
                else if (change.Property.Name == nameof(TransactionCascadeSubject.Probe))
                {
                    probeChanges.Add(change);
                }
            });

        // Act
        using (await context.BeginTransactionAsync(TransactionFailureHandling.BestEffort))
        {
            subject.Plain = "plain-pending";
            subject.SideEffect = "side-pending";
            subject.ExternalSuffix = "after";
            property.RecalculateDerivedProperty();

            // Assert
            Assert.Contains("plain-model|side-model", innerSubscriberReads);
            Assert.Equal("plain-model|side-model|after", Assert.Single(probeChanges).GetNewValue<string>());
        }

        probeChanges.Clear();
        subject.Plain = "plain-landed";
        Assert.Single(probeChanges);
        probeChanges.Clear();
        subject.SideEffect = "side-landed";
        Assert.Single(probeChanges);
    }

    [Fact]
    public void WhenAReentrantDerivedGetterFinishesWithoutAnotherRead_ThenAFollowingSubjectStillTracksDerivedProperties()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking();
        var reentrantSubject = new TransactionCascadeSubject(context)
        {
            Plain = "before",
            DerivedWithSetter = "d0"
        };
        var triggerInnerRecalculation = 0;
        reentrantSubject.ProbeEvaluator = subject =>
        {
            var result = subject.Plain;
            if (Interlocked.Exchange(ref triggerInnerRecalculation, 1) == 0)
            {
                subject.DerivedWithSetter = "d1";
            }

            return result;
        };

        var changes = new List<SubjectPropertyChange>();
        using var subscription = context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(changes.Add);

        // Act
        new PropertyReference(reentrantSubject, nameof(TransactionCascadeSubject.Probe))
            .RecalculateDerivedProperty();

        var unrelatedSubject = new Person(context)
        {
            FirstName = "John",
            LastName = "Doe"
        };
        changes.Clear();
        unrelatedSubject.FirstName = "Jane";

        // Assert
        var change = Assert.Single(changes, change =>
            change.Property.Subject == unrelatedSubject &&
            change.Property.Name == nameof(Person.FullName));
        Assert.Equal("John Doe", change.GetOldValue<string>());
        Assert.Equal("Jane Doe", change.GetNewValue<string>());
    }
}
