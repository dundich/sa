namespace Sa.HybridFileStorage;

public class HybridFileStorageNoAvailableException() : Exception("No storage available.");


public class HybridFileStorageAggregateException(IEnumerable<Exception> innerExceptions)
    : AggregateException("Operation failed for some available storages.", innerExceptions);


public class HybridFileStorageWritableException()
    : Exception("Cannot perform operation. All storage options are read-only.");
