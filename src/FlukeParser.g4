parser grammar FlukeParser;

options { tokenVocab=FlukeLexer; }

program   : statement* EOF ;
statement : COLON IDENTIFIER ;
