using Microsoft.AspNetCore.SignalR.Client;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

// Create a cancellation token source for graceful shutdown signaling
var cancellationTokenSource = new CancellationTokenSource();

// Global handler for unhandled exceptions (e.g., logic bugs or uncaught errors)
AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine("Unhandled exception:");
    Console.WriteLine(e.ExceptionObject.ToString());
    Console.ResetColor();
};

// Global handler for process exit (e.g., Environment.Exit, SIGTERM)
AppDomain.CurrentDomain.ProcessExit += (sender, e) =>
{
    Console.WriteLine("Process exit signal received. Cleaning up...");
    cancellationTokenSource.Cancel();
    cancellationTokenSource.Dispose();
};

// Handles unloading in environments like Docker or ASP.NET (triggered by SIGTERM or unload)
AssemblyLoadContext.Default.Unloading += (_) =>
{
    Console.WriteLine("Unloading triggered. Application domain is shutting down.");
    cancellationTokenSource.Cancel();
    cancellationTokenSource.Dispose();
};

// Parse and validate CLI arguments
var arguments = FormatArguments(args);
var hubUri = string.Empty;
var listenerUri = string.Empty;
var botId = string.Empty;

// Confirm and validate the required parameters
try
{
    // Confirm and clean the URI and port input values
    hubUri = ConfirmUri($"{arguments["HubUri"]}");
    listenerUri = $"{arguments["ListenerUri"]}";
    botId = $"{arguments["Id"]}";

    // Update the argument dictionary with normalized values
    arguments["HubUri"] = hubUri;
    arguments["ListenerUri"] = listenerUri;
    arguments["Id"] = botId;
}
catch (Exception ex)
{
    // Display error and exit if validation fails
    Console.WriteLine($"Error: {ex.Message}");

    // Exit with a non-zero status code to indicate failure
    Environment.Exit(1);
}

// Build the SignalR hub connection with keep-alive and automatic reconnection settings
var connection = new HubConnectionBuilder()
    .WithUrl($"{hubUri}/hub/v4/g4/bots")
    .WithKeepAliveInterval(TimeSpan.FromSeconds(10))
    .WithAutomaticReconnect()
    .Build();

// Handle Ctrl+C (SIGINT) for graceful shutdown
Console.CancelKeyPress += (sender, e) =>
{
    // Log the cancellation request and prevent the process from terminating immediately
    Console.WriteLine("Ctrl+C detected. Shutting down...");
    e.Cancel = true;

    // Cancel the cancellation token source to signal all tasks to stop
    cancellationTokenSource.Cancel();

    // Dispose of the cancellation token source to release resources
    cancellationTokenSource.Dispose();

    // Stop the SignalR connection before exiting
    connection.StopAsync().GetAwaiter().GetResult();
};

// Enrich the argument dictionary with runtime metadata (e.g., machine, OS, container flag)
AddMetadata(arguments);

// Attempt to connect to the hub (with retry logic)
await Connect(connection);

// Register the bot with the hub once connected
await Register(connection, arguments);

StartBotHttpListener(
    prefix: $"{listenerUri.TrimEnd('/')}/monitor/{botId}/",
    updateHandler: (message) => connection.InvokeAsync("UpdateBot", message, cancellationTokenSource.Token),
    cancellationToken: cancellationTokenSource.Token);

// Handle the SignalR connection closed event
// This is triggered when the connection is lost and not immediately recoverable (e.g., after retries fail)
connection.Closed += async (error) =>
{
    // Log the reason for closure if available
    Console.WriteLine(error?.GetBaseException().Message);

    // Inform the user that a reconnect attempt will be made
    Console.WriteLine("Connection closed. Attempting to reconnect...");

    // Wait briefly before retrying to reconnect
    await Task.Delay(2000);

    // Attempt to re-establish the SignalR connection
    await Connect(connection);
    await Register(connection, arguments);
};

// Handle the SignalR reconnecting event
// This is triggered when the client detects a loss of connection and begins attempting to reconnect
connection.Reconnecting += (error) =>
{
    // Log the root cause of the disconnection, if available
    Console.WriteLine(error?.GetBaseException().Message);

    // Notify that a reconnection attempt is underway
    Console.WriteLine("Reconnecting...");

    // Return a completed task since no additional async work is needed
    return Task.CompletedTask;
};

// Handle successful reconnection to the SignalR hub
// This event is triggered after the client has reconnected following a disconnection
connection.Reconnected += (connectionId) =>
{
    // Log the new connection ID after reconnection
    Console.WriteLine($"Reconnected. Connection ID: {connectionId}");

    // Return a completed task since no additional async work is needed
    return Task.CompletedTask;
};

// Write a message to the console when a bot receives an heartbeat signal
connection.On<string>("ReceiveHeartbeat", Console.WriteLine);

// Write a message to the console when a bot is registered
connection.On<string>("ReceiveRegisterBot", Console.WriteLine);

// Keep the application running
await Task.Delay(Timeout.Infinite, cancellationTokenSource.Token);

// Adds runtime metadata to the bot's registration argument dictionary.
// Includes connection ID, environment info, and container status flag.
static void AddMetadata(Dictionary<string, object> arguments)
{
    // Add machine hostname
    arguments["Machine"] = Environment.MachineName;

    // Add OS version details
    arguments["OsVersion"] = $"{Environment.OSVersion}";
}

// Validates that the provided URI string is a well-formed absolute HTTP or HTTPS URI.
// If the URI is invalid or uses an unsupported scheme, the process exits with an error message.
static string ConfirmUri(string uri)
{
    // Attempt to parse the string as an absolute URI
    var isUri = Uri.TryCreate(uri, UriKind.Absolute, out var uriOut);

    // Check if the URI is valid and uses http or https
    if (isUri && (uriOut?.Scheme == Uri.UriSchemeHttp || uriOut?.Scheme == Uri.UriSchemeHttps))
    {
        return uriOut.AbsoluteUri.TrimEnd('/');
    }

    // Throw an exception to indicate the error
    throw new UriFormatException($"Invalid URI (must be HTTP or HTTPS): {uri}");
}

// Attempts to connect to a SignalR hub, retrying every 5 seconds for up to 10 minutes.
// Logs each failure and exits if unable to connect within the allowed time.
static async Task Connect(HubConnection connection)
{
    await InvokeRetriableAction(
        actionName: "Connect",
        action: () => connection.StartAsync());
}

// Parses command-line arguments provided to the bot monitor process.
// Enforces the presence of required parameters and shows usage help if needed.
static Dictionary<string, object> FormatArguments(string[] args)
{
    // Define required parameters and their descriptions
    var requiredParams = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        { "HubUri", "The SignalR hub URI (e.g., http://localhost:9944/hub/v4/g4/bots)." },
        { "Name", "The human-readable name of the bot." },
        { "Type", "The type or category of the bot (e.g., File Listener Bot, Static Bot)." },
        { "ListenerUri", "The full endpoint URI where the listener receives status update requests." },
        { "Id", "The unique identifier of the bot instance used for hub registration and tracking." }
    };

    // Show help if the --help flag is passed
    if (args.Any(a => a.StartsWith("--help", StringComparison.OrdinalIgnoreCase)))
    {
        Console.WriteLine("Description:");
        Console.WriteLine("  Background process that syncs bot status with a SignalR hub.\n");
        Console.WriteLine("Usage:");
        Console.WriteLine("  dotnet G4.Bots.Monitor.dll --HubUri=<url> --Name=<name> --Type=<type>\n");

        // Display each option with its description
        Console.WriteLine("Options:");
        foreach (var param in requiredParams)
        {
            Console.WriteLine($"  --{param.Key,-12} {param.Value}");
        }

        // Show example usage
        Console.WriteLine("\nExample:");
        Console.WriteLine(" dotnet G4.Bots.Monitor.dll --HubUri=http://localhost:9944/hub/v4/g4/bots --Name=Bot123 --Type=\"Static Bot\"");

        // Exit after showing help
        Environment.Exit(0);
    }

    // Parse all --key=value arguments into a dictionary
    var argumentsCollection = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

    // Iterate through each argument
    foreach (var arg in args)
    {
        // Skip invalid argument formats
        if (!arg.StartsWith("--") || !arg.Contains('='))
        {
            continue;
        }

        // Split into key and value
        var split = arg[2..].Split('=', 2);
        var key = split[0];
        var value = split[1];

        // Add to parsedArgs only if it's a required parameter
        if (requiredParams.ContainsKey(key))
        {
            argumentsCollection[key] = value;
        }
    }

    // Identify any missing required parameters
    var missing = requiredParams.Keys.Except(argumentsCollection.Keys, StringComparer.OrdinalIgnoreCase).ToList();
    if (missing.Count == 0)
    {
        return argumentsCollection;
    }

    // Output which parameters are missing
    Console.WriteLine("Missing required parameters:");
    foreach (var key in missing)
    {
        Console.WriteLine($"--{key}: {requiredParams[key]}");
    }
    Console.WriteLine("\nUse --help to see full usage.");

    // Exit with error code
    Environment.Exit(1);

    // Should never hit this, but required for compilation
    throw new InvalidOperationException("Unreachable code executed after Environment.Exit");
}

// Sends a registration request to the SignalR hub using the "RegisterBot" method,
// passing the provided argument dictionary as payload.
static async Task Register(HubConnection connection, Dictionary<string, object> arguments)
{
    await InvokeRetriableAction(
        actionName: "RegisterBot",
        action: () => connection.InvokeAsync("RegisterBot", arguments));
}

// Repeatedly attempts to execute a given asynchronous action (e.g., a SignalR hub registration),
// retrying on failure until a timeout is reached.
static async Task InvokeRetriableAction(string actionName, Func<Task> action)
{
    // Interval between retry attempts (in seconds)
    const int retryIntervalSeconds = 5;

    // Maximum time to keep retrying (in minutes)
    const int timeoutMinutes = 10;

    // Determine the final time after which no further attempts should be made
    var timeout = DateTime.UtcNow.AddMinutes(timeoutMinutes);

    // Create a TimeSpan representing the retry interval
    var interval = TimeSpan.FromSeconds(retryIntervalSeconds);

    // Attempt the action repeatedly until either it succeeds or the timeout is reached
    while (DateTime.UtcNow < timeout)
    {
        try
        {
            Console.WriteLine($"Attempting '{actionName}'...");

            // Attempt the provided action
            await action();

            // If the action succeeds, log success and return
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"'{actionName}' succeeded.");
            Console.ResetColor();
            return;
        }
        catch (Exception e)
        {
            // Log the failure reason and inform about the retry
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"'{actionName}' failed: {e.Message}");
            Console.WriteLine($"Retrying in {retryIntervalSeconds} seconds...\n");
            Console.ResetColor();

            // Wait before next retry
            await Task.Delay(interval);
        }
    }

    // If the loop exits due to timeout, log a final error message
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"'{actionName}' failed after {timeoutMinutes} minutes of retries.");
    Console.WriteLine("Please verify the hub URL, check network connectivity, and ensure the server is running.");
    Console.ResetColor();
}

static void StartBotHttpListener(string prefix, Func<object, Task> updateHandler, CancellationToken cancellationToken)
{
    // Health‑check endpoint.
    // Always returns HTTP 200 with the plain‑text body “pong”.
    static async Task Ping(HttpListenerContext context, CancellationToken cancellationToken)
    {
        // Convert the literal response text to a UTF‑8 byte array.
        byte[] body = Encoding.UTF8.GetBytes(@"{""message"":""pong""}");

        // Set minimal headers so the client knows the request succeeded
        // and the payload is json.
        context.Response.StatusCode = (int)HttpStatusCode.OK; // 200
        context.Response.ContentType = "application/json; charset=utf-8";

        // Write the body to the output stream. The token lets the operation
        // abort cleanly if the client disconnects.
        await context.Response.OutputStream.WriteAsync(body, cancellationToken);
    }

    // Reads the entire request body, forwards it to <paramref name="updateHandler"/>,
    // and responds with either:
    // • HTTP 200 and “Update received” when <paramref name="updateHandler"/> succeeds  
    // • HTTP 500 and the exception message when it throws
    static async Task Update(
        HttpListenerContext context,
        Func<object, Task> updateHandler,
        CancellationToken cancellationToken)
    {
        // Read the request body as a string, using the encoding specified by the client.
        // HttpListener exposes the request body as a raw stream, so a StreamReader is needed.
        using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding);
        var body = await reader.ReadToEndAsync(cancellationToken);
        var jsonRequestBody = JsonSerializer.Deserialize<Dictionary<string, object>>(body);

        // Set content type to JSON, so the client knows what to expect.
        context.Response.ContentType = "application/json; charset=utf-8";

        try
        {
            // Delegate domain‑specific work to the supplied handler.
            // Any exception thrown here will be caught and turned into a 500 response.
            await updateHandler(jsonRequestBody);

            // Set status code to 200 OK, so the client knows the request was processed.
            context.Response.StatusCode = (int)HttpStatusCode.OK;

            // Convert the literal response text to a UTF‑8 byte array.
            var jsonResponseBody = Encoding.UTF8.GetBytes(@"{""message"":""Connected bot successfuly updated.""}");

            // Write the body to the output stream. The token lets the operation
            await context.Response.OutputStream.WriteAsync(jsonResponseBody, cancellationToken);
        }
        catch (Exception e)
        {
            // If the handler throws an exception, set the status code to 500
            context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;

            // Create an object to hold the error details
            var objectBody = new
            {
                error = e.GetType().Name,
                message = e.GetBaseException().StackTrace
            };

            // Serialize the object to a JSON string
            var jsonBody = JsonSerializer.Serialize(objectBody);

            // Convert the literal response text to a UTF‑8 byte array.
            var error = Encoding.UTF8.GetBytes(jsonBody);

            // Write the error to the output stream.
            await context.Response.OutputStream.WriteAsync(error, cancellationToken);
        }
    }

    Task.Run(async () =>
    {
        // Create a new HTTP listener instance
        var listener = new HttpListener();

        // Add the URL prefix to listen on (e.g., "http://localhost:5000/")
        listener.Prefixes.Add(prefix);

        // Start listening for incoming HTTP requests
        listener.Start();

        // Log the URL prefix to the console
        Console.WriteLine($"HTTP listener started on {prefix}");

        try
        {
            // Loop until cancellation is requested
            while (!cancellationToken.IsCancellationRequested)
            {
                // Wait for the next incoming request
                var context = await listener.GetContextAsync();

                // Determine if this is a GET request to /ping
                var isGet = context.Request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase);
                var isPing = isGet && context.Request.Url?.AbsolutePath.EndsWith("/ping", StringComparison.OrdinalIgnoreCase) == true;

                // Determine if this is a POST request to /update
                var isPost = context.Request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase);
                var isUpdate = isPost && context.Request.Url?.AbsolutePath.EndsWith("/update", StringComparison.OrdinalIgnoreCase) == true;

                // Handle ping: respond with "pong"
                if (isPing)
                {
                    await Ping(context, cancellationToken);
                }
                // Handle update: read body and pass to updateHandler
                else if (isUpdate)
                {
                    await Update(context, updateHandler, cancellationToken);
                }
                // Unknown route: return 404 Not Found
                else
                {
                    context.Response.StatusCode = 404;
                }

                // Close the response to send it back to the client
                context.Response.Close();
            }
        }
        catch (HttpListenerException) when (cancellationToken.IsCancellationRequested)
        {
            // Listener was stopped due to cancellation; ignore exception
        }
        finally
        {
            // Clean up resources by stopping and closing the listener
            listener.Stop();
            listener.Close();
            Console.WriteLine("HTTP listener stopped.");
        }
    }, cancellationToken);
}
