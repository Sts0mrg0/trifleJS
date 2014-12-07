﻿using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Net;
using System.IO;
using System.Text;
using System.Web;

namespace TrifleJS.API.Modules
{
    /// <summary>
    /// A HTTP Server class for use inside V8 engine
    /// </summary>
    public class WebServer
    {
        private static Dictionary<string, Listener> processes = new Dictionary<string, Listener>();
        private static int threadsPerListener = 3;

        public WebServer() 
        {
            port = String.Empty;
        }

        /// <summary>
        /// Opens a HTTP listener on specific TCP bindings
        /// </summary>
        /// <param name="bindings"></param>
        /// <param name="callbackId"></param>
        public bool _listen(string uri, string callbackId) {
            // Start & Run HTTP daemon
            try
            {
                // Initialize URI for binding
                int port; Uri binding;
                if (Int32.TryParse(uri, out port)) { binding = new Uri(String.Format("http://localhost:{0}/", port)); }
                else if (!uri.Contains("http")) { binding = new Uri(String.Format("http//{0}", uri)); }
                else { binding = new Uri(uri); }
                uri = binding.AbsoluteUri;
                // Already using binding? Replace it.
                if (processes.ContainsKey(uri))
                {
                    processes[uri].Stop();
                    processes.Remove(uri);
                }
                Listener listener = new Listener(callbackId);
                listener.listener.Prefixes.Add(uri);
                listener.listener.Start();
                processes.Add(uri, listener);
                this.port = binding.Port.ToString();
                Console.xdebug(String.Format("WebServer Listening on {0}", uri));
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// The port where the server is listening
        /// </summary>
        public string port { get; set; }

        /// <summary>
        /// Shuts down the server
        /// </summary>
        public void close() {
            foreach (Listener listener in processes.Values) {
                listener.Stop();
            }
            processes.Clear();
        }

        /// <summary>
        /// Processes all current requests
        /// </summary>
        internal static void ProcessConnections()
        {
            // Loop through active TCP bindings
            foreach (string uri in new List<string>(processes.Keys))
            {
                // Check each server for incoming connections
                Listener listener = processes[uri];
                if (listener != null && listener.listener != null)
                {
                    // Is it listening?
                    if (listener.listener.IsListening)
                    {
                        // Are there enough threads listening to incoming connections?
                        if (listener.threads < threadsPerListener)
                        {
                            // Add separate thread for filling up queue
                            listener.threads++;
                            listener.listener.BeginGetContext(delegate(IAsyncResult result)
                            {
                                try
                                {
                                    if (listener.listener != null)
                                    {
                                        // Check again if we are listening 
                                        // (might have been disconnected in meantime)
                                        HttpListenerContext context = listener.listener.EndGetContext(result);
                                        if (listener.listener.IsListening)
                                        {
                                            // Add connection to queue (asynchronously)
                                            Connection connection = new Connection(listener.callbackId, context);
                                            //Console.xdebug(String.Format("ProcessRequests:Queueing connection for {0}!", connection.id));
                                            // This will be processed in STA thread (below)
                                            // so that there are no memory conflicts in
                                            // callbacks to V8 environment.
                                            listener.connections.Add(connection.id, connection);
                                        }
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Console.error(String.Format("Error queueing connection: {0}", ex.Message));
                                }
                                finally
                                {
                                    listener.threads--;
                                }
                            }, listener.listener);
                        }
                        // Process incoming connection queue
                        // (in STA thread to avoid COM memory issues)
                        try
                        {
                            // Sometimes a new connection gets inserted
                            // into the queue asyncronously, causing
                            // the new List<string>() statement below to fail.
                            // In these cases we just ignore the error
                            // and wait for the next pass to read the queue.
                            foreach (string connectionId in new List<string>(listener.connections.Keys))
                            {
                                Connection connection = listener.connections[connectionId];
                                if (connection != null && !connection.isProcessing)
                                {
                                    // Start processing
                                    connection.isProcessing = true;
                                    if (connection.request != null && connection.response != null)
                                    {
                                        // Make callback to V8 environment
                                        Console.xdebug(String.Format("Processing connection {0}..", connectionId));
                                        Callback.Execute(connection.callbackId, connectionId);
                                    }
                                }
                            }
                        } catch  {}
                    }
                    else { 
                        // Not listening? Shutdown and remove from process queue..
                        listener.Stop();
                        processes.Remove(uri);
                    }
                }
            }
        }

        /// <summary>
        /// Finds a connection in the list of active server processes
        /// </summary>
        /// <param name="connectionId"></param>
        /// <returns></returns>
        private static Connection Find(string connectionId)
        { 
            // Loop through active http bindings
            foreach (string uri in new List<string>(processes.Keys))
            {
                // Check each server for incoming connections
                Listener listener = processes[uri];
                if (listener != null && listener.listener != null)
                {
                    if (listener.connections.ContainsKey(connectionId)) 
                    {
                        return listener.connections[connectionId];
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// Removes a connection in the list of active server processes
        /// </summary>
        /// <param name="connectionId"></param>
        /// <returns></returns>
        private static void Remove(string connectionId)
        {
            // Loop through active TCP bindings
            foreach (string uri in new List<string>(processes.Keys))
            {
                // Check each server for incoming connections
                Listener listener = processes[uri];
                if (listener != null && listener.listener != null)
                {
                    try
                    {
                        if (listener.connections.ContainsKey(connectionId)) {
                            listener.connections.Remove(connectionId);
                        }
                    }
                    catch { }
                }
            }
        }


        /// <summary>
        /// Gets the request object for a connection
        /// </summary>
        /// <param name="connectionId"></param>
        /// <returns></returns>
        public Request _getRequest(string connectionId) {
            Connection connection = Find(connectionId);
            if (connection != null) {
                return connection.request;
            }
            return null;
        }

        /// <summary>
        /// Gets the response object for a connection
        /// </summary>
        /// <param name="connectionId"></param>
        /// <returns></returns>
        public Response _getResponse(string connectionId) {
            Connection connection = Find(connectionId);
            if (connection != null)
            {
                return connection.response;
            }
            return null;
        }

        /// <summary>
        /// An internal class representing a http listener
        /// </summary>
        private class Listener {
            public Listener(string callbackId) {
                this.listener = new HttpListener();
                this.callbackId = callbackId;
            }
            public Listener(HttpListener listener, string callbackId) {
                this.listener = listener;
                this.callbackId = callbackId;
            }
            public void Stop()
            {
                connections.Clear();
                if (this.listener != null)
                {
                    this.listener.Stop();
                    this.listener = null;
                }
            }
            public HttpListener listener;
            public string callbackId;
            public int threads;
            public Dictionary<string, Connection> connections = new Dictionary<string, Connection>();
        }

        /// <summary>
        /// The request object used by V8 engine
        /// </summary>
        public class Request { 
        
            public Request(HttpListenerRequest request) {
                this.request = request;
                using (StreamReader reader = new StreamReader(request.InputStream, request.ContentEncoding))
                {
                    this.rawPost = reader.ReadToEnd().Trim();
                    if (request.ContentType != null && request.ContentType.Contains("application/x-www-form-urlencoded")) {
                        Dictionary<string, object> data = new Dictionary<string, object> ();
                        NameValueCollection form = HttpUtility.ParseQueryString(this.rawPost);
                        foreach (string key in form)
                        {
                            data.Add(key, form[key]);
                        }
                        this.post = data;
                    } else {
                        this.post = rawPost;
                    }
                }
            }

            private HttpListenerRequest request;

            /// <summary>
            /// Defines the request method ('GET', 'POST', etc.)
            /// </summary>
            public string method {
                get { return request.HttpMethod; }
            }

            /// <summary>
            /// The path part and query string part (if any) of the request URL
            /// </summary>
            public string url {
                get { return request.Url.PathAndQuery; }
            }

            /// <summary>
            /// The actual HTTP version
            /// </summary>
            public string httpVersion {
                get { return request.ProtocolVersion.ToString(); }
            }

            /// <summary>
            /// All of the HTTP headers as key-value pairs
            /// </summary>
            public Dictionary<string, object> headers {
                get {
                    Dictionary<string, object> result = new Dictionary<string, object>();
                    foreach (string key in request.Headers.AllKeys) {
                        result.Add(key, request.Headers[key]);
                    }
                    result.Add("User-Agent", request.UserAgent);
                    return result;
                }
            }

            /// <summary>
            /// The request body (only for 'POST' and 'PUT' method requests)
            /// </summary>
            public object post { get; set; }

            /// <summary>
            /// If the Content-Type header is set to 'application/x-www-form-urlencoded' 
            /// (the default for form submissions), the original contents of post will 
            /// be stored in this extra property (postRaw) and then post will be 
            /// automatically updated with a URL-decoded version of the data.
            /// </summary>
            public string rawPost { get; set; }

        }

        /// <summary>
        /// The response object used by V8 engine
        /// </summary>
        public class Response {

            public Response(HttpListenerResponse response, string connectionId) {
                this.response = response;
                this.connectionId = connectionId;
                this.headers = new Dictionary<string, object>();
                this.response.SendChunked = true;
                this.isHeaderSent = false;
            }

            private HttpListenerResponse response;
            private string connectionId;
            private bool isHeaderSent = false;

            /// <summary>
            /// HTTP Status code to send to client
            /// </summary>
            public int statusCode
            {
                get { return response.StatusCode; }
                set { response.StatusCode = value; }
            }

            /// <summary>
            /// Write the response to outgoing stream.
            /// </summary>
            /// <param name="text"></param>
            public void write(string text) {
                // First request requires syncing headers
                if (!this.isHeaderSent) {
                    response.Headers.Clear();
                    foreach (string key in headers.Keys)
                    {
                        response.AddHeader(key, headers[key].ToString());
                    }
                }
                // Write to outgoing stream
                byte[] buffer = Encoding.UTF8.GetBytes(text);
                response.OutputStream.Write(buffer, 0, buffer.Length);
                response.OutputStream.Flush();
                this.isHeaderSent = true;
            }

            /// <summary>
            /// Sends header information to the browser
            /// </summary>
            /// <param name="statusCode"></param>
            /// <param name="headers"></param>
            public void writeHead(int statusCode, Dictionary<string, object> headers) {
                // Set status code and headers
                this.response.StatusCode = statusCode;
                // Use default headers if none found
                if (headers == null) { headers = this.headers; }
                else { this.headers = headers; }
                // Send a newline response.
                // Assuming request.SendChunked = true,
                // this will send off the header information
                this.write(Environment.NewLine);
            }

            /// <summary>
            /// Sets a header
            /// </summary>
            /// <param name="name"></param>
            /// <param name="value"></param>
            public void setHeader(string name, string value) {
                headers.Add(name, value);
            }

            /// <summary>
            /// Returns a header
            /// </summary>
            /// <param name="name"></param>
            /// <returns></returns>
            public string header(string name) {
                return headers[name].ToString();
            }

            /// <summary>
            /// Collection of response headers to send to browser
            /// </summary>
            public Dictionary<string, object> headers { get; set; }

            /// <summary>
            /// Sets encoding for HTTP response
            /// </summary>
            /// <param name="encoding"></param>
            public void setEncoding(string encoding) {
                try
                {
                    Encoding enc = Encoding.GetEncoding(encoding);
                    response.ContentEncoding = enc;
                }
                catch { }
            }

            /// <summary>
            /// Closes connection
            /// </summary>
            public void close() {
                response.Close();
                WebServer.Remove(this.connectionId);
            }

            /// <summary>
            /// Ensures header information is sent and closes connection
            /// </summary>
            public void closeGracefully() {
                if (!this.isHeaderSent) {
                    this.response.StatusCode = 200;
                    this.write(Environment.NewLine);
                }
                this.close();
            }
        }

        /// <summary>
        /// A connection object used for queueing incoming connections
        /// asynchronously & processing them on STA thread.
        /// </summary>
        public class Connection {

            public Connection(string callbackId, HttpListenerContext context) {
                this.callbackId = callbackId;
                this.id = Utils.newUid();
                this.isProcessing = false;
                this.request = new Request(context.Request);
                this.response = new Response(context.Response, this.id);
            }

            public string id;
            public bool isProcessing;
            public string callbackId;
            public Request request;
            public Response response;

        }
    }
}
