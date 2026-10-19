// Код подключения к агенту стенда (адрес 127.0.0.1) — зашифрованным, как его хранит удалённый профиль окна.
using eViSTool.Core.Server.Remote;
var agents = args[0];
var s = RemoteAccess.Load("srv1", agents);
using var cert = RemoteAccess.EnsureCertificate("srv1", agents);
var code = new ConnectionCode("127.0.0.1", s.Port, s.Key, RemoteAccess.Fingerprint(cert));
Console.WriteLine(RemoteSecret.Protect(code));
