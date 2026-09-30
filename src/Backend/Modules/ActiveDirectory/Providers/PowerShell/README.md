# PowerShell provider - intentionally empty

Version 1 needs **no** PowerShell. Every Active Directory action in this application can be done over LDAPS,
which is the preferred implementation, so the LDAP provider handles all of them.
See `docs/DECISIONS.md` ("Provider choice per action") for the action-by-action reasoning.

If a future action genuinely cannot be done over LDAP, add the PowerShell implementation here behind
`IDirectoryProvider`, following the rules in the specification (section 12.2):

* only the ActiveDirectory module, through a runspace inside this folder
* a fixed list of allowed cmdlets, values passed as parameters (never built into script strings)
* target objects by objectGUID, passwords as `SecureString` only
* `-WhatIf` as part of the dry run wherever the cmdlet supports it
