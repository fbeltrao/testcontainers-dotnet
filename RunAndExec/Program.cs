using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Docker.DotNet;
using Docker.DotNet.Models;

namespace RunAndExec
{
    class Program
    {
        static async Task Main(string[] args)
        {
           
            DockerClient client = new DockerClientConfiguration(new Uri("npipe://./pipe/docker_engine"))
                 .CreateClient();


            var createResponse = await client.Containers.CreateContainerAsync(new CreateContainerParameters()
            {
                Image = "rabbitmq",
                ExposedPorts = new Dictionary<string, EmptyStruct>
                {
                    { "5762/tcp", default }
                },
                AttachStderr = true,
                AttachStdin = true,
                AttachStdout = true,               
            });


            await client.Containers.StartContainerAsync(
                createResponse.ID,
                new ContainerStartParameters(),
                default);

            var exec = await client.Containers.ExecCreateContainerAsync(
                createResponse.ID,
                new ContainerExecCreateParameters()
                {
                    AttachStderr = true,
                    //AttachStdin = true,
                    AttachStdout = true,
                    //Tty = true,
                    Cmd = new string[] { "rabbitmq-plugins", "enable", "rabbitmq_consistent_hash_exchange" },
                },
                default);

            await client.Containers.StartContainerExecAsync(exec.ID, default);
        }
    }
}
