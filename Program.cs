using System;
using System.Data.Common;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Newtonsoft.Json;
using Npgsql;
using StackExchange.Redis;

namespace Worker
{
    public class Program
    {
        public static int Main(string[] args)
        {

            // Connect to services
            try
            {
                // Set redisHost
                var redisHostEnv = Environment.GetEnvironmentVariable("REDIS_HOST");
                string redisHost;
                if (redisHostEnv == null)
                {
                    redisHost = "redis";
                }
                else
                {
                    redisHost = redisHostEnv;
                }

                // Set postgresServer
                var postgresServerEnv = Environment.GetEnvironmentVariable("POSTGRES_SERVER");
                string postgresServer;
                if (postgresServerEnv == null)
                {
                    postgresServer = "db";
                }
                else
                {
                    postgresServer = postgresServerEnv;
                }

                // Set postgresUsername
                var postgresUsernameEnv = Environment.GetEnvironmentVariable("POSTGRES_USERNAME");
                string postgresUsername;
                if (postgresUsernameEnv == null)
                {
                    postgresUsername = "postgres";
                }
                else
                {
                    postgresUsername = postgresUsernameEnv;
                }

                // Set postgresPassword
                var postgresPasswordEnv = Environment.GetEnvironmentVariable("POSTGRES_PASSWORD");
                string postgresPassword;
                if (postgresPasswordEnv == null)
                {
                    postgresPassword = "postgres";
                }
                else
                {
                    postgresPassword = postgresPasswordEnv;
                }

                // Create DB connections
                Console.WriteLine($"PSQL Server: {postgresServer}\n");
                Console.WriteLine($"PSQL Username: {postgresUsername}\n");
                Console.WriteLine($"PSQL Password: {postgresPassword}\n");
                Console.WriteLine($"Connection String:\n");
                Console.WriteLine($"Server={postgresServer};Username={postgresUsername};Password={postgresPassword}\n");
                Console.WriteLine($"Redis Host: {redisHost}\n");
                var pgsql = OpenDbConnection($"Server={postgresServer};Username={postgresUsername};Password={postgresPassword};");
                var redisConn = OpenRedisConnection(redisHost);
                var redis = redisConn.GetDatabase();

                // Keep alive is not implemented in Npgsql yet. This workaround was recommended:
                // https://github.com/npgsql/npgsql/issues/1214#issuecomment-235828359
                var keepAliveCommand = pgsql.CreateCommand();
                keepAliveCommand.CommandText = "SELECT 1";

                var definition = new { vote = "", voter_id = "" };
                while (true)
                {
                    // Slow down to prevent CPU spike, only query each 100ms
                    Thread.Sleep(100);

                    // Reconnect redis if down
                    if (redisConn == null || !redisConn.IsConnected) {
                        Console.WriteLine("Reconnecting Redis");
                        redisConn = OpenRedisConnection("redis");
                        redis = redisConn.GetDatabase();
                    }
                    string json = redis.ListLeftPopAsync("votes").Result;
                    if (json != null)
                    {
                        var vote = JsonConvert.DeserializeAnonymousType(json, definition);
                        Console.WriteLine($"Processing vote for '{vote.vote}' by '{vote.voter_id}'");
                        // Reconnect DB if down
                        if (!pgsql.State.Equals(System.Data.ConnectionState.Open))
                        {
                            Console.WriteLine("Reconnecting DB");
                            pgsql = OpenDbConnection("Server=db;Username=postgres;Password=postgres;");
                        }
                        else
                        { // Normal +1 vote requested
                            UpdateVote(pgsql, vote.voter_id, vote.vote);
                        }
                    }
                    else
                    {
                        keepAliveCommand.ExecuteNonQuery();
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.ToString());
                return 1;
            }
        }

        private static NpgsqlConnection OpenDbConnection(string connectionString)
        {
            NpgsqlConnection connection;

            while (true)
            {
                try
                {
                    connection = new NpgsqlConnection(connectionString);
                    connection.Open();
                    break;
                }
                catch (SocketException)
                {
                    Console.Error.WriteLine("Waiting for db to become available.");
                    Thread.Sleep(1000);
                }
                catch (DbException)
                {
                    Console.Error.WriteLine("Waiting for db to become available.");
                    Thread.Sleep(1000);
                }
            }

            Console.Error.WriteLine("Connected to db");

            var command = connection.CreateCommand();
            command.CommandText = @"CREATE TABLE IF NOT EXISTS votes (
                                        id VARCHAR(255) NOT NULL UNIQUE,
                                        vote VARCHAR(255) NOT NULL
                                    )";
            command.ExecuteNonQuery();

            return connection;
        }

        private static ConnectionMultiplexer OpenRedisConnection(string hostname)
        {
            // Use IP address to workaround https://github.com/StackExchange/StackExchange.Redis/issues/410
            var ipAddress = GetIp(hostname);
            Console.WriteLine($"Found redis at {ipAddress}");

            while (true)
            {
                try
                {
                    Console.Error.WriteLine("Connecting to redis");
                    return ConnectionMultiplexer.Connect(ipAddress);
                }
                catch (RedisConnectionException)
                {
                    Console.Error.WriteLine("Waiting for redis");
                    Thread.Sleep(1000);
                }
            }
        }

        private static string GetIp(string hostname)
            => Dns.GetHostEntryAsync(hostname)
                .Result
                .AddressList
                .First(a => a.AddressFamily == AddressFamily.InterNetwork)
                .ToString();

        private static void UpdateVote(NpgsqlConnection connection, string voterId, string vote)
        {
            var command = connection.CreateCommand();
            try
            {
                command.CommandText = "INSERT INTO votes (id, vote) VALUES (@id, @vote)";
                command.Parameters.AddWithValue("@id", voterId);
                command.Parameters.AddWithValue("@vote", vote);
                command.ExecuteNonQuery();
            }
            catch (DbException)
            {
                command.CommandText = "UPDATE votes SET vote = @vote WHERE id = @id";
                command.ExecuteNonQuery();
            }
            finally
            {
                command.Dispose();
            }
        }
    }
}