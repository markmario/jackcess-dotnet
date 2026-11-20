@AGENTS.md

## Claude Code

`AGENTS.md`, imported above, is the one set of instructions every agent here shares. Change it there, by pull
request. This file holds only what is particular to Claude Code.

- **Sub-agents.** A sub-agent may start without these instructions, and its shell starts in the session's
  working directory - and returns there after every command - not in its own clone. Its brief carries the rules
  its task needs, or has it read `AGENTS.md` first; names its clone by full path; and has it give full paths in
  every command and script.
- **Memory.** Auto memory is for what is particular to a machine or to the owner: paths, clones, preferences. A
  rule that holds for every clone belongs in `AGENTS.md`, and outstanding work belongs in an issue.
- **Scripts on Windows.** Write a script that holds backslashes, `$` or regular expressions to a file with the
  file tools, and run the file. Passed through a shell's quoting - an unquoted heredoc, a double-quoted string -
  they can be changed on the way.
