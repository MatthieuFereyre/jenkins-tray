#!/usr/bin/perl
#
# Prints the FileVersion of a Windows binary, or nothing when it carries no version resource.
#
# build-installer.sh reads the version back out of the installer it has just written: that number
# is what tells one build from the next, and a typo in the .nsi would otherwise ship silently. It
# has to run on Linux, where nothing reads a VS_VERSIONINFO block — no python in the image.
#
# Written for the MSI packaging this replaced, where wixl left every File.Version empty and
# Windows Installer therefore refused to overwrite the binaries of the previous version.
#
# Usage: pe-version.pl <file>   ->   "1.1.7.0" on stdout, or nothing.
use strict;
use warnings;

my $path = shift or die "usage: pe-version.pl <file>\n";
open my $fh, '<:raw', $path or die "$path: $!\n";
my $data = do { local $/; <$fh> };
close $fh;

# The version lives in the .rsrc section. Scanning the whole file for the VS_FIXEDFILEINFO
# signature would also match a binary embedded as data — a resource of another executable — so
# the walk stops at the section that is allowed to hold it.
exit 0 if length($data) < 0x40 || substr($data, 0, 2) ne 'MZ';
my $pe = unpack 'V', substr($data, 0x3c, 4);
exit 0 if length($data) < $pe + 24 || substr($data, $pe, 4) ne "PE\0\0";

my $sections = unpack 'v', substr($data, $pe + 6, 2);
my $optional_size = unpack 'v', substr($data, $pe + 20, 2);
my $table = $pe + 24 + $optional_size;

my ($start, $size);
for my $i (0 .. $sections - 1) {
    my $header = substr($data, $table + $i * 40, 40);
    last if length($header) < 40;
    my $name = unpack 'Z8', $header;
    next unless $name eq '.rsrc';
    ($size, $start) = unpack 'V V', substr($header, 16, 8);
    last;
}
exit 0 unless defined $start && $size;

# VS_FIXEDFILEINFO: signature 0xfeef04bd, struct version, then the file version as two dwords,
# each holding two 16-bit fields. dwProductVersion follows, and is deliberately not read: the
# installer compares what MsiGetFileVersion returns, which is the file version.
my $rsrc = substr($data, $start, $size);
my $at = index $rsrc, pack('V', 0xfeef04bd);
exit 0 if $at < 0 || $at + 16 > length $rsrc;

my ($ms, $ls) = unpack 'V V', substr($rsrc, $at + 8, 8);
printf "%d.%d.%d.%d\n", $ms >> 16, $ms & 0xffff, $ls >> 16, $ls & 0xffff;
