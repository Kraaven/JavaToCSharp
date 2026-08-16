package com.example.small;

import java.util.List;
import java.util.ArrayList;

public class Small {
    private int value = 42;
    private String name = "small";

    public Small(int value, String name) {
        this.value = value;
        this.name = name;
    }

    public int getValue() {
        return value;
    }

    public String getName() {
        return name;
    }

    public int sum(List<Integer> numbers) {
        int total = 0;
        for (int n : numbers) {
            total += n;
        }
        return total;
    }

    public static void main(String[] args) {
        Small s = new Small(1, "one");
        System.out.println(s.getName());
    }
}

enum Color {
    RED, GREEN, BLUE
}

interface Greeter {
    String greet(String name);
}
