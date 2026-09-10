# Context service cardinality

Some services represent one authority for a resolved context cone. Such a service implements
`IUniqueContextService<TContract>`, where `TContract` identifies the constrained authority.

For each authority contract, zero or one distinct instance is valid. The same object reached
through several context paths is valid. Two different objects are invalid even when `Equals`
reports equality. One object may implement several authority contracts.

The initial unique authorities are `ILifecycleInterceptor`, `ISubjectRegistry`, and
`SubjectTransactionInterceptor`. Ordered extension collections such as `ILifecycleHandler` and
`IPropertyLifecycleHandler` remain multi-service.

Validation runs while services are resolved and examines every reachable raw service, not only the
requested service type. Consequently an unrelated query can expose a conflicting authority.
`TryGetService<T>()` may throw; its `Try` prefix describes the no-result case only.

Successful validation is cached on immutable context state. Service or fallback mutation publishes
a new state without the marker and invalidates upstream caches. Invalid topology may therefore
exist after mutation until its next resolution.

Configure and compose one shared application context for the ordinary object graph. When several
contexts intentionally expose non-unique services, compose them before configuring the consuming
context so idempotent helpers reuse reachable authorities. Independently configured contexts that
contribute different lifecycle, registry, or transaction authorities cannot share one resolved
cone.
