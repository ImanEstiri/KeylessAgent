# KeylessAgent CLI

**A coding agent that runs on your own DeepSeek login. No API key.**

`keyless` is the command-line front end for [KeylessAgent](https://github.com/ImanEstiri/KeylessAgent). It drives your logged-in DeepSeek web chat through Chrome, applies the model's replies to your workspace using the strict AgentScript command language, and keeps your work on a protected agent branch.

> Automating a web UI may violate the provider's terms of use and can get your account limited. This project is not affiliated with DeepSeek.

## Installation

Requires the **.NET 10** runtime and Chrome.

```bash
dotnet tool install --global ImanSoftware.KeylessAgent.Cli
```

## Usage

```bash
keyless run "Add xUnit tests for the markdown parser"
keyless                              # interactive session, one chat reused
keyless resume "continue with the failing test"
keyless undo                         # restore the newest backup batch
keyless config                       # show / create keyless.json
```

Start Chrome with `chrome --remote-debugging-port=9222` and make sure you are logged in to chat.deepseek.com, or let KeylessAgent launch a persistent profile for you.

Full documentation — AgentScript, configuration, selectors, security model — lives in the main repository:

**[github.com/ImanEstiri/KeylessAgent](https://github.com/ImanEstiri/KeylessAgent)**

## License

MIT — see [LICENSE](https://github.com/ImanEstiri/KeylessAgent/blob/main/LICENSE.txt).
