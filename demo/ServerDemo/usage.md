## 打包成独立运行的可执行文件

```zsh
dotnet publish -c Release -r osx-x64 -p:PublishSingleFile=true --self-contained true 
```

其中 `-r` 参数可以改为 `win-x64` 或者 `linux-x64`。  
打包成功后，根据打包命令的输出，可以找到可独立运行 exe 文件的位置，一般在 `./bin/Release/net8.0/osx-x64/publish`

## 查看可执行的服务器应用

```zsh
./ServerDemo list
```

## 启动 Demo 服务器

启动打包后可独立运行的服务器，需要先进入到可执行文件所在的目录，然后：

```zsh
./ServerDemo run --path ServerDemo.ServerTestings.Mar07.DemoServer --jsonArgs '{"ServerEndpoint":"0.0.0.0:2666","Fps":20, "GameDuration":180}'
```

启动非独立运行的服务器，需要先进入到源代码 ServerDemo.csproj 所在目录（即这个文件所在目录），然后：

```zsh
dotnet run -- run --path ServerDemo.ServerTestings.Mar07.DemoServer --jsonArgs '{"ServerEndpoint":"0.0.0.0:2666","Fps":20, "GameDuration":180}'
```

## 启动模拟客户端

```zsh
dotnet run -- run --path ServerDemo.ServerTestings.Mar07.DemoTestClient --jsonArgs '{"Server":"127.0.0.1:2666","PlayerId":6,"RoomCapacity":6}'
```