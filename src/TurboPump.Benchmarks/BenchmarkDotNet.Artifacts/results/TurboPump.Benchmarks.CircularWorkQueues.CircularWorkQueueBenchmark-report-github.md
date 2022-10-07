``` ini

BenchmarkDotNet=v0.12.1, OS=Windows 10.0.19044
AMD Ryzen 7 1700, 1 CPU, 16 logical and 8 physical cores
.NET Core SDK=6.0.201
  [Host]     : .NET Core 6.0.3 (CoreCLR 6.0.322.12309, CoreFX 6.0.322.12309), X64 RyuJIT
  Job-PKQZYJ : .NET Core 6.0.3 (CoreCLR 6.0.322.12309, CoreFX 6.0.322.12309), X64 RyuJIT

InvocationCount=1  UnrollFactor=1  

```
|    Method |     Mean |    Error |   StdDev |   Median | Gen 0 | Gen 1 | Gen 2 | Allocated |
|---------- |---------:|---------:|---------:|---------:|------:|------:|------:|----------:|
|      Push | 29.39 ns | 0.315 ns | 0.409 ns | 29.41 ns |     - |     - |     - |         - |
|   PopOnly | 63.03 ns | 1.238 ns | 1.474 ns | 62.30 ns |     - |     - |     - |      26 B |
| StealOnly | 33.89 ns | 0.648 ns | 0.908 ns | 33.19 ns |     - |     - |     - |         - |
