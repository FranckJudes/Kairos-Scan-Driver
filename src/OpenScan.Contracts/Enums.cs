namespace OpenScan.Contracts;

public enum JobStatus
{
    Created,
    Validating,
    Ready,
    Running,
    Paused,
    Cancelled,
    Error,
    RecoverableError,
    Completed
}

public enum PageStatus
{
    Acquired,
    Processing,
    Ready,
    Persisted,
    Error,
    NotAcquired
}

public enum ScannerStatus
{
    Unknown,
    Idle,
    Busy,
    Disconnected,
    Error
}

public enum ConnectionType
{
    Unknown,
    Usb,
    Network,
    Virtual
}

public enum ErrorCode
{
    ScannerNotFound,
    JobNotFound,
    ScannerBusy,
    ScannerDisconnected,
    DriverError,
    BackendError,
    PaperJam,
    NoPaper,
    MultiFeed,
    CapabilityUnsupported,
    InvalidConfiguration,
    InvalidJobState,
    AcquisitionError,
    ProcessingError,
    StorageError,
    Timeout,
    Cancelled
}
