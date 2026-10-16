namespace DCx.Core.Broker;

public sealed class ProcessBrokerPolicy
{
    private readonly string _workspaceRoot;
    private readonly StringComparison _pathComparison;

    public ProcessBrokerPolicy(string workspaceRoot, bool caseInsensitivePaths = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        _workspaceRoot = EnsureTrailingSeparator(Path.GetFullPath(workspaceRoot));
        _pathComparison = caseInsensitivePaths ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    }

    public BrokerAuthorization Evaluate(BrokerRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.Operation switch
        {
            BrokerOperation.LaunchControlledFixture when request.TargetPath is null =>
                BrokerAuthorization.Allow(request.Operation, null, "Repository-owned controlled fixture."),
            BrokerOperation.LaunchCalculator when request.TargetPath is null =>
                BrokerAuthorization.Allow(request.Operation, null, "Allowlisted Windows Calculator."),
            BrokerOperation.LaunchNotepad => EvaluateWorkspaceFile(request, mustExist: false),
            BrokerOperation.InspectPathReadOnly => EvaluateWorkspaceFile(request, mustExist: true),
            _ => BrokerAuthorization.Deny(request.Operation, "The operation or its arguments are not permitted."),
        };
    }

    private BrokerAuthorization EvaluateWorkspaceFile(BrokerRequest request, bool mustExist)
    {
        if (string.IsNullOrWhiteSpace(request.TargetPath))
        {
            return BrokerAuthorization.Deny(request.Operation, "A workspace-scoped target path is required.");
        }

        string canonicalPath;
        try
        {
            canonicalPath = Path.GetFullPath(request.TargetPath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return BrokerAuthorization.Deny(request.Operation, "The target path is invalid.");
        }

        if (!IsInsideWorkspace(canonicalPath))
        {
            return BrokerAuthorization.Deny(request.Operation, "The target path is outside the approved workspace.");
        }

        if (ContainsReparsePoint(canonicalPath))
        {
            return BrokerAuthorization.Deny(request.Operation, "Reparse-point and junction targets are not permitted by this broker.");
        }

        if (mustExist && !File.Exists(canonicalPath) && !Directory.Exists(canonicalPath))
        {
            return BrokerAuthorization.Deny(request.Operation, "The inspection target does not exist.");
        }

        return BrokerAuthorization.Allow(request.Operation, canonicalPath, "Canonical workspace path approved.");
    }

    private bool IsInsideWorkspace(string path)
    {
        if (string.Equals(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                _workspaceRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                _pathComparison))
        {
            return true;
        }

        return EnsureTrailingSeparator(path).StartsWith(_workspaceRoot, _pathComparison) ||
               path.StartsWith(_workspaceRoot, _pathComparison);
    }

    private bool ContainsReparsePoint(string path)
    {
        var workspace = _workspaceRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string? current = path;
        while (!string.IsNullOrWhiteSpace(current) &&
               current.StartsWith(_workspaceRoot, _pathComparison))
        {
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                return true;
            }

            current = Path.GetDirectoryName(current);
            if (string.Equals(current, workspace, _pathComparison))
            {
                break;
            }
        }

        return false;
    }

    private static string EnsureTrailingSeparator(string path) =>
        path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;
}
