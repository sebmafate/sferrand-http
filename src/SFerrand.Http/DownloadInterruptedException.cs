namespace SFerrand.Http;

/// <summary>
/// A download failed after some bytes had already been written to the destination, so it cannot be retried: the stream cannot be rewound, and writing the same
/// bytes twice would corrupt it.
/// </summary>
/// <remarks>It is an <see cref="IOException"/> on purpose, but the client never retries this one, unlike the connection errors that happen before the first byte.</remarks>
public sealed class DownloadInterruptedException : IOException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="bytesWritten">How many bytes reached the destination.</param>
    /// <param name="innerException">The failure.</param>
    public DownloadInterruptedException(long bytesWritten, Exception innerException)
        : base($"The download was interrupted after {bytesWritten} bytes.", innerException) => BytesWritten = bytesWritten;

    /// <summary>How many bytes reached the destination before the failure.</summary>
    public long BytesWritten { get; }
}
