"""Streams the files listed in pour_files.txt (relative to H:\\MapLibrary) as a tar into D:\\MapLibrary on USER."""
import os, socket, subprocess, tarfile
HERE = os.path.dirname(os.path.abspath(__file__))
LIB = r'H:\MapLibrary'
host = socket.gethostbyname('USER')
ssh = ['ssh', '-i', r'C:\Users\Leshe\.map-remote\user1_ed25519', '-o', r'UserKnownHostsFile=C:\Users\Leshe\.map-remote\known_hosts',
       '-o', 'HostKeyAlias=192.168.31.44', '-o', 'BatchMode=yes', f'user1@{host}', 'tar -xf - -C D:/MapLibrary']
rels = [l.strip() for l in open(os.path.join(HERE, 'pour_files.txt'), encoding='utf-8') if l.strip()]
proc = subprocess.Popen(ssh, stdin=subprocess.PIPE)
with tarfile.open(fileobj=proc.stdin, mode='w|', format=tarfile.PAX_FORMAT) as tar:
    for rel in rels:
        tar.add(os.path.join(LIB, rel), arcname=rel.replace(os.sep, '/'))
proc.stdin.close()
print('sent', len(rels), 'exit', proc.wait())
