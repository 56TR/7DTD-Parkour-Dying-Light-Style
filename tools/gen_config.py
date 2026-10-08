# Regenerate Config/parkour.xml from the defaults (and comments) in src/Core/Tuning.cs.
# Usage: python -I tools/gen_config.py <mod root>
import os
import re
import sys

root = sys.argv[1]
src = open(os.path.join(root, 'src', 'Core', 'Tuning.cs'), encoding='utf-8').read()
body = src.split('public string LoadXml')[0]
out = [
    '<?xml version="1.0" encoding="UTF-8"?>',
    '<!-- Parkour Movement tuning. Speeds m/s, times s, heights m above the feet, damage in hit points.',
    '     Values marked DL2 come from Dying Light 2 scripts/player/player_variables.scr, speeds scaled x1.3 (7DTD sprint 6.6 m/s vs DL2 5.0).',
    '     Edit, then in game console: pk reload   (or pk set <name> <value> to try one value live) -->',
    '<parkour>',
]
for line in body.splitlines():
    s = line.strip()
    m = re.match(r'//\s*(.+)$', s)
    if m and not s.startswith('///'):
        out.append('\n\t<!-- ' + m.group(1) + ' -->')
        continue
    m = re.match(r'public (float|bool) (\w+) = ([^;]+);\s*(//\s*(.*))?', s)
    if m:
        v = m.group(3).replace('f', '') if m.group(1) == 'float' else m.group(3)
        c = ('  <!-- ' + m.group(5) + ' -->') if m.group(5) else ''
        out.append('\t<property name="' + m.group(2) + '" value="' + v + '"/>' + c)
out.append('</parkour>')
open(os.path.join(root, 'Config', 'parkour.xml'), 'w', encoding='utf-8').write('\n'.join(out) + '\n')
print('properties:', sum(1 for l in out if '<property' in l))
