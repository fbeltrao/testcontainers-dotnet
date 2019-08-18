using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using Docker.DotNet.Models;
using Polly;

namespace TestContainers.Core.Containers
{
    public class Container
    {
        static readonly UTF8Encoding Utf8EncodingWithoutBom = new UTF8Encoding(false);
        readonly DockerClient _dockerClient;
        private MultiplexedStream _stream;

        string _containerId { get; set; }
        public string DockerImageName { get; set; }
        public int[] ExposedPorts { get; set; }
        public (int ExposedPort, int PortBinding)[] PortBindings { get; set; }
        public (string key, string value)[] EnvironmentVariables { get; set; }
        public (string key, string value)[] Labels { get; set; }
        public ContainerInspectResponse ContainerInspectResponse { get; set; }
        public (string SourcePath, string TargetPath, string Type)[] Mounts { get; set; }
        public string[] Commands { get; set; }

        public Container() =>
            _dockerClient = DockerClientFactory.Instance.Client();

        public async Task Start()
        {
            _containerId = await Create();
            await TryStart();
        }

        async Task TryStart()
        {
            var started = await _dockerClient.Containers.StartContainerAsync(_containerId, new ContainerStartParameters());
            if (started)
            {
                using (var logs = await _dockerClient.Containers.GetContainerLogsAsync(_containerId,
                    new ContainerLogsParameters
                    {
                        ShowStderr = true,
                        ShowStdout = true,
                    }, default(CancellationToken)))
                {
                    using (var reader = new StreamReader(logs, Utf8EncodingWithoutBom))
                    {
                        string nextLine;
                        while ((nextLine = await reader.ReadLineAsync()) != null)
                        {
                            Debug.WriteLine(nextLine);
                        }
                    }
                }
            }

            await WaitUntilContainerStarted();
        }

        protected virtual async Task WaitUntilContainerStarted()
        {
            var retryUntilContainerStateIsRunning = Policy
                                .HandleResult<ContainerInspectResponse>(c => !c.State.Running)
                                .RetryForeverAsync();

            var containerInspectPolicy = await Policy
                .TimeoutAsync(TimeSpan.FromMinutes(1))
                .WrapAsync(retryUntilContainerStateIsRunning)
                .ExecuteAndCaptureAsync(async () => ContainerInspectResponse = await _dockerClient.Containers.InspectContainerAsync(_containerId));

            if (containerInspectPolicy.Outcome == OutcomeType.Failure)
                throw new ContainerLaunchException("Container startup failed", containerInspectPolicy.FinalException);
        }

        async Task<string> Create()
        {
            var progress = new Progress<JSONMessage>(async (m) =>
            {
                Console.WriteLine(m.Status);
                if (m.Error != null)
                    await Console.Error.WriteLineAsync(m.ErrorMessage);

            });

            var tag = DockerImageName.Split(':').Last();
            var imagesCreateParameters = new ImagesCreateParameters
            {
                FromImage = DockerImageName,
                Tag = tag,
            };

            var images = await this._dockerClient.Images.ListImagesAsync(new ImagesListParameters { MatchName = DockerImageName });

            if (!images.Any())
            {
                await this._dockerClient.Images.CreateImageAsync(imagesCreateParameters, new AuthConfig(), progress, CancellationToken.None);
            }

            var createContainersParams = ApplyConfiguration();
            var containerCreated = await _dockerClient.Containers.CreateContainerAsync(createContainersParams);



            return containerCreated.ID;
        }

        CreateContainerParameters ApplyConfiguration()
        {
            var exposedPorts = ExposedPorts?.ToList() ?? new List<int>();

            var cfg = new Config
            {
                Image = DockerImageName,
                Env = EnvironmentVariables?.Select(ev => $"{ev.key}={ev.value}").ToList(),
                ExposedPorts = exposedPorts.ToDictionary(e => $"{e}/tcp", e => default(EmptyStruct)),
                Labels = Labels?.ToDictionary(l => l.key, l => l.value),
                Tty = false,
                Cmd = Commands,
                AttachStderr = true,
                AttachStdout = true,
                AttachStdin = true,
            };

            var bindings = PortBindings?.ToDictionary(p => p.ExposedPort, p => p.PortBinding) ?? exposedPorts.ToDictionary(e => e, e => e);

            var portBindings = new Dictionary<string, IList<PortBinding>>();
            foreach (var binding in bindings)
            {
                portBindings.Add($"{binding.Key}/tcp", new[] { new PortBinding { HostPort = binding.Value.ToString() } });
            }

            return new CreateContainerParameters(cfg)
            {
                HostConfig = new HostConfig
                {
                    PortBindings = portBindings,
                    Mounts = Mounts?.Select(m => new Mount
                    {
                        Source = m.SourcePath,
                        Target = m.TargetPath,
                        Type = m.Type,
                    }).ToList(),
                }
            };
        }

        public async Task Stop()
        {
            if (string.IsNullOrWhiteSpace(_containerId)) return;

            await _dockerClient.Containers.StopContainerAsync(ContainerInspectResponse.ID, new ContainerStopParameters());
            await _dockerClient.Containers.RemoveContainerAsync(ContainerInspectResponse.ID, new ContainerRemoveParameters());
        }


        public async Task ExecuteCommand(params string[] command)
        {
            var containerExecCreateParams = new ContainerExecCreateParameters
            {
                AttachStderr = true,
                AttachStdout = true,
                Cmd = command,

                //AttachStdin = true,
                //Tty = true,
            };

            var response = await _dockerClient.Containers.ExecCreateContainerAsync(_containerId, containerExecCreateParams);

            await _dockerClient.Containers.StartContainerExecAsync(response.ID);
        }

        public string GetDockerHostIpAddress()
        {
            var dockerHostUri = _dockerClient.Configuration.EndpointBaseUri;

            switch (dockerHostUri.Scheme)
            {
                case "http":
                case "https":
                case "tcp":
                    return dockerHostUri.Host;
                case "npipe": //will have to revisit this for LCOW/WCOW
                case "unix":
                    return File.Exists("/.dockerenv")
                        ? ContainerInspectResponse.NetworkSettings.Gateway
                        : "localhost";
                default:
                    return null;
            }
        }

        public async Task ExecInRunnningContainer2(string command, CancellationToken cancellationToken = default)
        {
            var config = new ContainerAttachParameters
            {
                Stream = false,
                Stderr = true,
                Stdin = true,
                Stdout = true
            };

            var buffer = new byte[1024];
            using (var stream = await _dockerClient.Containers.AttachContainerAsync(_containerId, true, config, cancellationToken))
            {                
                var writerBuffer = Encoding.ASCII.GetBytes(command + "\n");
                await stream.WriteAsync(writerBuffer, 0, writerBuffer.Length, cancellationToken);

                var result = await stream.ReadOutputAsync(buffer, 0, buffer.Length, cancellationToken);
                do
                {
                    Console.Write(Encoding.UTF8.GetString(buffer, 0, result.Count));
                }
                while (!result.EOF);
            }

            Console.WriteLine("Worked!");
        }

        public async Task ExecInRunnningContainer(string command, CancellationToken cancellationToken = default)
        {
            var writerBuffer = Encoding.ASCII.GetBytes(command + "\n");
            await _stream.WriteAsync(writerBuffer, 0, writerBuffer.Length, cancellationToken);

            var buffer = new byte[1024];
            var result = await _stream.ReadOutputAsync(buffer, 0, buffer.Length, cancellationToken);
            do
            {
                Console.Write(Encoding.UTF8.GetString(buffer, 0, result.Count));
            }
            while (!result.EOF);

        }
    }
}
