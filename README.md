# Operational Tech Integration Project

This is an FT Optix proof-of-concept project which integrates the following components:

- a MicroLogix 850 PLC
- FactoryTalk Optix
- Fiix Maintenance software

The goal of this project is to demonstrate data flowing from an actual PLC to Optix and finally to Fiix CMMS software.

The use cases covered by this project include: 

- Triggering an asset as offline or online based on a value change in a PLC tag.
- Continuous analog data coming into Fiix as a meter reading (Pressure values)
- Continuous discrete data coming into Fiix as a meter reading (Number of machine restarts)

## Fiix Optix Libraries

This project makes heavy use of the Fiix Optix libraries. For more information regarding these libraries, please refer to [FactoryTalk Optix® Fiix® Library documentation](https://literature.rockwellautomation.com/idc/groups/literature/documents/rm/info-rm007_-en-p.pdf).
